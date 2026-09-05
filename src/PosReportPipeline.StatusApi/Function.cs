using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using PosReportPipeline.Shared.Models;
using PosReportPipeline.Shared.Parsing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.StatusApi;

/// <summary>
/// GET /status/{flightId}. Returns the most recent state known for a flight.
///
/// The results table is checked first, because a calculated result supersedes
/// the report it came from. If the calculator has not caught up yet the caller
/// gets the report state instead -- that is the pipeline being asynchronous,
/// not an error.
/// </summary>
public class Function
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _reportsTable;
    private readonly string _resultsTable;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonDynamoDBClient(),
               new AmazonS3Client(),
               RequiredEnv("REPORTS_BUCKET"),
               RequiredEnv("REPORTS_TABLE"),
               RequiredEnv("RESULTS_TABLE"))
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(
        IAmazonDynamoDB dynamo, IAmazonS3 s3,
        string bucket, string reportsTable, string resultsTable)
    {
        _dynamo = dynamo;
        _s3 = s3;
        _bucket = bucket;
        _reportsTable = reportsTable;
        _resultsTable = resultsTable;
    }

    public async Task<APIGatewayProxyResponse> Handler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var flightId = request.PathParameters is not null
                       && request.PathParameters.TryGetValue("flightId", out var raw)
            ? raw.Trim().ToUpperInvariant()
            : string.Empty;

        if (string.IsNullOrEmpty(flightId))
        {
            return Json(HttpStatusCode.BadRequest, new { message = "flightId is required." });
        }

        var latestResult = await LatestItem(_resultsTable, flightId);
        if (latestResult is not null)
        {
            var result = await ReadResult(latestResult);
            if (result is not null)
            {
                return Json(HttpStatusCode.OK, new
                {
                    flightId = result.FlightId,
                    status = "CALCULATED",
                    timestamp = PosObjectKeys.FormatMilliseconds(result.Timestamp),
                    remainingFlightTimeMinutes = result.RemainingFlightTimeMinutes,
                    estimatedFuelAtArrivalKg = result.EstimatedFuelAtArrivalKg,
                    lowFuelWarning = result.LowFuelWarning,
                });
            }

            // The row exists but its object does not. Fall through to the
            // report state rather than 500 -- the caller still gets something
            // true, and the missing object is logged for us to chase.
            context.Logger.LogError(
                $"Result row for {flightId} points at an unreadable attachment.");
        }

        var latestReport = await LatestItem(_reportsTable, flightId);
        if (latestReport is not null)
        {
            return Json(HttpStatusCode.OK, new
            {
                flightId,
                status = "PARSED",
                timestamp = latestReport.TryGetValue("timestamp", out var ts) ? ts.S : null,
                message = "Report stored; calculation has not completed yet.",
            });
        }

        context.Logger.LogInformation($"No state found for {flightId}.");
        return Json(HttpStatusCode.NotFound, new
        {
            flightId,
            message = $"No reports found for flight '{flightId}'.",
        });
    }

    /// <summary>
    /// Newest item for a flight. Both tables sort by an ISO-8601 UTC timestamp,
    /// which sorts lexicographically in the same order as chronologically, so
    /// the last key is the latest report.
    /// </summary>
    private async Task<Dictionary<string, AttributeValue>?> LatestItem(string tableName, string flightId)
    {
        var response = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = tableName,
            KeyConditionExpression = "flightId = :flightId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":flightId"] = new() { S = flightId },
            },
            ScanIndexForward = false,
            Limit = 1,
        });

        return response.Items is null || response.Items.Count == 0 ? null : response.Items[0];
    }

    private async Task<CalculationResult?> ReadResult(Dictionary<string, AttributeValue> row)
    {
        if (!row.TryGetValue("attachment", out var attachment) || attachment.S is null)
        {
            return null;
        }

        try
        {
            using var response = await _s3.GetObjectAsync(_bucket, attachment.S);
            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();

            return JsonSerializer.Deserialize<CalculationResult>(json, PosJson.Options);
        }
        catch (Exception ex) when (ex is AmazonS3Exception or JsonException)
        {
            return null;
        }
    }

    private static string RequiredEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set.");

    private static APIGatewayProxyResponse Json(HttpStatusCode status, object body) =>
        new()
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = JsonSerializer.Serialize(body),
        };
}
