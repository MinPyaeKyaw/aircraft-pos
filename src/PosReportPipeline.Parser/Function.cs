using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using PosReportPipeline.Shared.Models;
using PosReportPipeline.Shared.Parsing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.Parser;

/// <summary>
/// Triggered by an S3 object landing under pos/. Parses the raw report, writes
/// the structured JSON under attachment/, indexes it in the reports table, and
/// queues the flight for calculation.
/// </summary>
public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonSQS _sqs;
    private readonly string _bucket;
    private readonly string _tableName;
    private readonly string _queueUrl;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonS3Client(),
               new AmazonDynamoDBClient(),
               new AmazonSQSClient(),
               RequiredEnv("REPORTS_BUCKET"),
               RequiredEnv("REPORTS_TABLE"),
               RequiredEnv("QUEUE_URL"))
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(
        IAmazonS3 s3, IAmazonDynamoDB dynamo, IAmazonSQS sqs,
        string bucket, string tableName, string queueUrl)
    {
        _s3 = s3;
        _dynamo = dynamo;
        _sqs = sqs;
        _bucket = bucket;
        _tableName = tableName;
        _queueUrl = queueUrl;
    }

    public async Task Handler(S3Event evnt, ILambdaContext context)
    {
        foreach (var record in evnt.Records)
        {
            // S3 event keys are URL-encoded.
            var key = WebUtility.UrlDecode(record.S3.Object.Key);
            await ProcessOne(key, context);
        }
    }

    private async Task ProcessOne(string key, ILambdaContext context)
    {
        // The receipt time is recovered from the key rather than read from the
        // clock. A report carries no year or month, so parsing it here with a
        // fresh clock would produce a different flight ID from the one the API
        // already returned for any report that crosses a month boundary
        // between ingest and parse -- and the caller would poll an ID that
        // never appears.
        if (!PosObjectKeys.TryParseRawKey(key, out var flightId, out var receivedAt))
        {
            context.Logger.LogError($"Ignoring object with unrecognised key format: {key}");
            return;
        }

        string raw;
        using (var response = await _s3.GetObjectAsync(_bucket, key))
        using (var reader = new StreamReader(response.ResponseStream))
        {
            raw = await reader.ReadToEndAsync();
        }

        if (!PosReportParser.TryParse(raw, receivedAt, out var parsed, out var error))
        {
            // Permanent: the same bytes will never parse. Returning normally
            // stops Lambda retrying it to no purpose. Nothing is lost -- the
            // raw object stays under pos/ and can be replayed once the parser
            // handles whatever this message contained.
            context.Logger.LogError($"Unparseable report at {key}: {error}");
            return;
        }

        var report = parsed!;

        if (!string.Equals(report.FlightId, flightId, StringComparison.Ordinal))
        {
            // Should be impossible: both are derived the same way from the same
            // inputs. If it ever happens, the key wins, because that is the ID
            // the caller was given.
            context.Logger.LogError(
                $"Flight ID mismatch for {key}: key says '{flightId}', parse says '{report.FlightId}'. "
                + "Using the key.");
            report = report with { FlightId = flightId };
        }

        var attachmentKey = PosObjectKeys.AttachmentKey(report.FlightId, report.Timestamp);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = attachmentKey,
            ContentBody = JsonSerializer.Serialize(report, PosJson.Options),
            ContentType = "application/json",
        });

        // The attachment is written before the index row, so a row never points
        // at an object that does not exist yet.
        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["flightId"] = new() { S = report.FlightId },
                ["timestamp"] = new() { S = PosObjectKeys.FormatSeconds(report.Timestamp) },
                ["attachment"] = new() { S = attachmentKey },
            },
        });

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = JsonSerializer.Serialize(
                new CalculationRequest
                {
                    FlightId = report.FlightId,
                    Timestamp = PosObjectKeys.FormatSeconds(report.Timestamp),
                },
                PosJson.Options),
        });

        context.Logger.LogInformation(
            $"Parsed {report.FlightId} at {PosObjectKeys.FormatSeconds(report.Timestamp)}; queued for calculation.");
    }

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}
