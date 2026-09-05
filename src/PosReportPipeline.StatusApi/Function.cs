using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.StatusApi;

/// <summary>
/// Serves the latest position and recent track for a flight.
///
/// One DynamoDB query answers both.
/// </summary>
public class Function
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

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

        if (!TryReadLimit(request, out var limit, out var limitError))
        {
            return Json(HttpStatusCode.BadRequest, new { message = limitError });
        }

        var response = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "flightId = :fid",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":fid"] = new() { S = flightId },
            },
            // Descending, so Limit keeps the NEWEST reports. Ascending would
            // silently return the oldest `limit` reports and call the last of
            // them "latest", which is wrong for any flight longer than `limit`.
            ScanIndexForward = false,
            Limit = limit,
        });

        if (response.Items is null || response.Items.Count == 0)
        {
            context.Logger.LogInformation($"No reports for {flightId}.");
            return Json(HttpStatusCode.NotFound,
                new { message = $"No reports found for flight '{flightId}'." });
        }

        // Newest first from DynamoDB; reverse so the track reads chronologically.
        var track = response.Items.Select(ToDto).Reverse().ToList();

        return Json(HttpStatusCode.OK, new
        {
            flightId,
            reportCount = track.Count,
            latest = track[^1],
            track,
        });
    }

    private static bool TryReadLimit(APIGatewayProxyRequest request, out int limit, out string? error)
    {
        limit = DefaultLimit;
        error = null;

        if (request.QueryStringParameters is null
            || !request.QueryStringParameters.TryGetValue("limit", out var raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            error = "limit must be a positive integer.";
            return false;
        }

        limit = Math.Min(parsed, MaxLimit);
        return true;
    }

    private static Dictionary<string, object?> ToDto(Dictionary<string, AttributeValue> item)
    {
        var dto = new Dictionary<string, object?>();

        foreach (var (name, value) in item)
        {
            dto[name] = value switch
            {
                { N: not null } => double.Parse(value.N, CultureInfo.InvariantCulture),
                { S: not null } => value.S,
                _ => null,
            };
        }

        return dto;
    }

    private static APIGatewayProxyResponse Json(HttpStatusCode status, object body) =>
        new()
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = JsonSerializer.Serialize(body, JsonOptions),
        };
}
