using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using PosReportPipeline.Shared.Geo;
using PosReportPipeline.Shared.Models;

// CalculationResult has a property of the same name, which shadows the type
// inside object initialisers. The alias keeps every reference unambiguous.
using CalcStatus = PosReportPipeline.Shared.Models.CalculationStatus;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.Calculator;

/// <summary>
/// Enriches parsed reports with distance and bearing to the destination and
/// writes them to DynamoDB.
///
/// Failures are classified before they are handled. A malformed message or an
/// unknown destination cannot succeed on retry, so neither is reported as a
/// batch failure. Only transient faults are, so that SQS retries them and
/// eventually parks them in the DLQ.
/// </summary>
public class Function
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonDynamoDBClient(),
               Environment.GetEnvironmentVariable("TABLE_NAME")
               ?? throw new InvalidOperationException("TABLE_NAME is not set."))
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(IAmazonDynamoDB dynamo, string tableName)
    {
        _dynamo = dynamo;
        _tableName = tableName;
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
            catch (JsonException ex)
            {
                // Permanent: this message body will never deserialise. Reporting
                // it as a failure would only cycle it to the DLQ.
                context.Logger.LogError(
                    $"Discarding unreadable message {message.MessageId}: {ex.Message}");
            }
            catch (Exception ex)
            {
                // Assume transient (throttling, timeouts) and let SQS retry.
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
        var report = JsonSerializer.Deserialize<ParsedPosReport>(message.Body, JsonOptions)
                     ?? throw new JsonException("Message body deserialised to null.");

        var result = Calculate(report);

        if (result.CalculationStatus == CalcStatus.UnknownDestination)
        {
            // Stored anyway: the position is real data, and a gap in the
            // catalog is no reason to lose it.
            context.Logger.LogWarning(
                $"Unknown destination '{report.DestinationIcao}' for {report.FlightId}; "
                + "storing without distance.");
        }

        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = ToItem(result),
        });

        context.Logger.LogInformation(
            $"Stored {result.FlightId} at {result.ReportedAt:o} ({result.CalculationStatus}).");
    }

    /// <summary>Pure. No I/O, so it is directly unit-testable.</summary>
    public static CalculationResult Calculate(ParsedPosReport report)
    {
        var common = new CalculationResult
        {
            FlightId = report.FlightId,
            ReportedAt = report.ReportedAtUtc,
            Callsign = report.Callsign,
            Latitude = report.Latitude,
            Longitude = report.Longitude,
            AltitudeFeet = report.AltitudeFeet,
            DestinationIcao = report.DestinationIcao,
            CalculationStatus = CalcStatus.UnknownDestination,
            CalculatedAt = DateTimeOffset.UtcNow,
            SourceKey = report.SourceKey,
        };

        if (!AirportCatalog.TryGet(report.DestinationIcao, out var destination))
        {
            return common;
        }

        var distanceKm = Haversine.DistanceKm(
            report.Latitude, report.Longitude, destination!.Latitude, destination.Longitude);

        var bearing = Haversine.InitialBearingDegrees(
            report.Latitude, report.Longitude, destination.Latitude, destination.Longitude);

        return common with
        {
            DestinationName = destination.Name,
            DistanceToDestinationKm = Math.Round(distanceKm, 3),
            DistanceToDestinationNm = Math.Round(Haversine.ToNauticalMiles(distanceKm), 3),
            InitialBearingDegrees = Math.Round(bearing, 3),
            CalculationStatus = CalcStatus.Ok,
        };
    }

    private static Dictionary<string, AttributeValue> ToItem(CalculationResult r)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["flightId"] = new() { S = r.FlightId },
            ["reportedAt"] = new() { S = r.ReportedAt.ToString("o") },
            ["callsign"] = new() { S = r.Callsign },
            ["latitude"] = Number(r.Latitude),
            ["longitude"] = Number(r.Longitude),
            ["altitudeFeet"] = Number(r.AltitudeFeet),
            ["destinationIcao"] = new() { S = r.DestinationIcao },
            ["calculationStatus"] = new() { S = r.CalculationStatus },
            ["calculatedAt"] = new() { S = r.CalculatedAt.ToString("o") },
        };

        // Nulls are omitted rather than written as NULL attributes, so an
        // unknown-destination item is simply missing its distance fields.
        if (r.DestinationName is not null)
        {
            item["destinationName"] = new AttributeValue { S = r.DestinationName };
        }

        if (r.SourceKey is not null)
        {
            item["sourceKey"] = new AttributeValue { S = r.SourceKey };
        }

        if (r.DistanceToDestinationKm is { } km)
        {
            item["distanceToDestinationKm"] = Number(km);
        }

        if (r.DistanceToDestinationNm is { } nm)
        {
            item["distanceToDestinationNm"] = Number(nm);
        }

        if (r.InitialBearingDegrees is { } bearing)
        {
            item["initialBearingDegrees"] = Number(bearing);
        }

        return item;
    }

    private static AttributeValue Number(double value) =>
        new() { N = value.ToString("R", CultureInfo.InvariantCulture) };
}
