using System.Net;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using PosReportPipeline.Shared.Parsing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.Parser;

/// <summary>
/// Reads raw reports from S3, parses them, and publishes structured reports to
/// SQS.
///
/// A parse failure is permanent, so the object is copied to the quarantine
/// prefix and the invocation returns successfully. Throwing would make Lambda
/// retry a message that can never succeed.
/// </summary>
public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly string _quarantinePrefix;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonS3Client(),
               new AmazonSQSClient(),
               Environment.GetEnvironmentVariable("QUEUE_URL")
               ?? throw new InvalidOperationException("QUEUE_URL is not set."),
               Environment.GetEnvironmentVariable("QUARANTINE_PREFIX") ?? "quarantine/")
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(IAmazonS3 s3, IAmazonSQS sqs, string queueUrl, string quarantinePrefix)
    {
        _s3 = s3;
        _sqs = sqs;
        _queueUrl = queueUrl;
        _quarantinePrefix = quarantinePrefix;
    }

    public async Task Handler(S3Event evnt, ILambdaContext context)
    {
        foreach (var record in evnt.Records)
        {
            var bucket = record.S3.Bucket.Name;

            // S3 event keys are URL-encoded: "a b.txt" arrives as "a+b.txt".
            var key = WebUtility.UrlDecode(record.S3.Object.Key);

            await ProcessOne(bucket, key, context);
        }
    }

    private async Task ProcessOne(string bucket, string key, ILambdaContext context)
    {
        string raw;
        using (var response = await _s3.GetObjectAsync(bucket, key))
        using (var reader = new StreamReader(response.ResponseStream))
        {
            raw = await reader.ReadToEndAsync();
        }

        if (!PosReportParser.TryParse(raw, key, out var report, out var error))
        {
            context.Logger.LogError($"Quarantining s3://{bucket}/{key}: {error}");
            await Quarantine(bucket, key, error!);
            return; // Permanent failure. Do not throw; do not retry.
        }

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = JsonSerializer.Serialize(report),
        });

        context.Logger.LogInformation($"Parsed {report!.FlightId} from s3://{bucket}/{key}");
    }

    private async Task Quarantine(string bucket, string key, string reason)
    {
        var fileName = key.Split('/').Last();

        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = key,
            DestinationBucket = bucket,
            DestinationKey = $"{_quarantinePrefix}{DateTimeOffset.UtcNow:yyyy/MM/dd}/{fileName}",
            MetadataDirective = S3MetadataDirective.REPLACE,
            Metadata =
            {
                // Truncated: S3 caps total user metadata at 2 KB.
                ["parse-error"] = reason.Length > 512 ? reason[..512] : reason,
                ["source-key"] = key,
            },
        });
    }
}
