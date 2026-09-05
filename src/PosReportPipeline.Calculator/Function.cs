using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using PosReportPipeline.Shared.Geo;
using PosReportPipeline.Shared.Models;
using PosReportPipeline.Shared.Parsing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.Calculator;

/// <summary>
/// Triggered by the queue. Loads the parsed report, works out how far the
/// aircraft still has to fly and how much fuel it should land with, and stores
/// the result under results/ plus an index row in the results table.
/// </summary>
public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _bucket;
    private readonly string _reportsTable;
    private readonly string _resultsTable;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonS3Client(),
               new AmazonDynamoDBClient(),
               RequiredEnv("REPORTS_BUCKET"),
               RequiredEnv("REPORTS_TABLE"),
               RequiredEnv("RESULTS_TABLE"))
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(
        IAmazonS3 s3, IAmazonDynamoDB dynamo,
        string bucket, string reportsTable, string resultsTable)
    {
        _s3 = s3;
        _dynamo = dynamo;
        _bucket = bucket;
        _reportsTable = reportsTable;
        _resultsTable = resultsTable;
    }

    public async Task<SQSBatchResponse> Handler(SQSEvent evnt, ILambdaContext context)
    {
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var message in evnt.Records)
        {
            try
            {
                await ProcessOne(message, context);
            }
            catch (PermanentCalculationException ex)
            {
                // Cannot succeed on retry, so it is not reported as a failure.
                // Doing so would burn three receives and land it in the DLQ for
                // no reason.
                context.Logger.LogError($"Dropping message {message.MessageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Assume transient (throttling, timeouts, a partial outage) and
                // let SQS redeliver, then the DLQ catch it.
                context.Logger.LogError($"Retryable failure on {message.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure
                {
                    ItemIdentifier = message.MessageId,
                });
            }
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    private async Task ProcessOne(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        CalculationRequest request;
        try
        {
            request = JsonSerializer.Deserialize<CalculationRequest>(message.Body, PosJson.Options)
                      ?? throw new JsonException("Message body deserialised to null.");
        }
        catch (JsonException ex)
        {
            throw new PermanentCalculationException($"Unreadable message body: {ex.Message}");
        }

        var report = await LoadReport(request, context);
        var result = Calculate(report, DateTimeOffset.UtcNow);

        var resultKey = PosObjectKeys.ResultKey(result.FlightId, result.Timestamp);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = resultKey,
            ContentBody = JsonSerializer.Serialize(result, PosJson.Options),
            ContentType = "application/json",
        });

        // Object first, then the index row, so a row never points at an object
        // that is not there yet.
        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _resultsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["flightId"] = new() { S = result.FlightId },
                ["timestamp"] = new() { S = PosObjectKeys.FormatMilliseconds(result.Timestamp) },
                ["attachment"] = new() { S = resultKey },
                ["lowFuelWarning"] = new() { BOOL = result.LowFuelWarning },
            },
        });

        if (result.LowFuelWarning)
        {
            context.Logger.LogWarning(
                $"LOW_FUEL_WARNING for {result.FlightId}: projected arrival fuel "
                + $"{result.EstimatedFuelAtArrivalKg} kg after {result.RemainingFlightTimeMinutes} min.");
        }

        context.Logger.LogInformation(
            $"Calculated {result.FlightId}: {result.RemainingFlightTimeMinutes} min remaining, "
            + $"{result.EstimatedFuelAtArrivalKg} kg at arrival; stored at {resultKey}.");
    }

    private async Task<ParsedPosReport> LoadReport(CalculationRequest request, ILambdaContext context)
    {
        var item = await _dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _reportsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["flightId"] = new() { S = request.FlightId },
                ["timestamp"] = new() { S = request.Timestamp },
            },
            // The parser writes the row before sending the message, but a
            // default eventually-consistent read can still miss it. A strongly
            // consistent read removes that race rather than relying on retries
            // to paper over it.
            ConsistentRead = true,
        });

        if (item.Item is null || item.Item.Count == 0)
        {
            // Transient by assumption: the row may simply not exist yet if this
            // message somehow overtook its writer. Retrying is the right move,
            // and the DLQ catches it if the row never appears.
            throw new InvalidOperationException(
                $"No report row for {request.FlightId} at {request.Timestamp}.");
        }

        if (!item.Item.TryGetValue("attachment", out var attachment) || attachment.S is null)
        {
            throw new PermanentCalculationException(
                $"Report row for {request.FlightId} at {request.Timestamp} has no attachment key.");
        }

        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, attachment.S);
            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();

            return JsonSerializer.Deserialize<ParsedPosReport>(json, PosJson.Options)
                   ?? throw new PermanentCalculationException(
                       $"Attachment {attachment.S} deserialised to null.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            context.Logger.LogError($"Attachment {attachment.S} is missing.");
            throw new PermanentCalculationException($"Attachment {attachment.S} does not exist.");
        }
        catch (JsonException ex)
        {
            throw new PermanentCalculationException(
                $"Attachment {attachment.S} is not valid report JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// The whole calculation, as a pure function of the report and a clock
    /// reading. No I/O, so it is unit-tested directly with no AWS mocks.
    /// </summary>
    public static CalculationResult Calculate(ParsedPosReport report, DateTimeOffset calculatedAtUtc)
    {
        if (!AirportCatalog.TryGet(report.Destination, out var destination))
        {
            // Permanent: no amount of retrying adds an airport to a hardcoded
            // table. It needs a code change, so it is logged loudly and the
            // message is dropped rather than cycled through the DLQ.
            throw new PermanentCalculationException(
                $"Destination '{report.Destination}' is not in the airport catalog.");
        }

        var distanceNm = Haversine.DistanceNm(
            report.Latitude, report.Longitude, destination!.Latitude, destination.Longitude);

        var remainingFlightTimeHours = distanceNm / report.GroundSpeedKnots;
        var estimatedFuelAtArrivalKg =
            report.FuelOnBoardKg - (report.FuelFlowKgPerHour * remainingFlightTimeHours);

        return new CalculationResult
        {
            FlightId = report.FlightId,
            Timestamp = calculatedAtUtc,
            Input = new CalculationInput
            {
                CurrentLatitude = report.Latitude,
                CurrentLongitude = report.Longitude,
                Destination = report.Destination,
                GroundSpeedKnots = report.GroundSpeedKnots,
                FuelOnBoardKg = report.FuelOnBoardKg,
                FuelFlowKgPerHour = report.FuelFlowKgPerHour,
            },
            RemainingFlightTimeMinutes = (int)Math.Round(remainingFlightTimeHours * 60),
            EstimatedFuelAtArrivalKg = Math.Round(estimatedFuelAtArrivalKg),
            LowFuelWarning = estimatedFuelAtArrivalKg < 0,
        };
    }

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");
}

/// <summary>
/// A failure that retrying cannot fix. Separating it from every other exception
/// is what keeps the DLQ meaningful: only genuinely transient problems reach it.
/// </summary>
public sealed class PermanentCalculationException : Exception
{
    public PermanentCalculationException(string message) : base(message) { }
}
