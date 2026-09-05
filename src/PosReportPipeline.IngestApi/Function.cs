using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using PosReportPipeline.Shared.Parsing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.IngestApi;

/// <summary>
/// POST /pos-reports. Validates a raw POS report, stores it under the pos/
/// prefix, and returns the flight ID the caller can poll with.
///
/// The object is stored verbatim: nothing downstream can lose the original
/// message, and a parser fix can be replayed against everything already
/// received.
/// </summary>
public class Function
{
    private const int MaxBodyBytes = 8 * 1024;

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonS3Client(),
               Environment.GetEnvironmentVariable("REPORTS_BUCKET")
               ?? throw new InvalidOperationException("REPORTS_BUCKET is not set."))
    {
    }

    /// <summary>Used by tests.</summary>
    public Function(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public async Task<APIGatewayProxyResponse> Handler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        var body = DecodeBody(request);

        if (string.IsNullOrWhiteSpace(body))
        {
            return Problem(HttpStatusCode.BadRequest, "Request body is empty.");
        }

        if (Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
        {
            return Problem(HttpStatusCode.BadRequest, $"Report exceeds {MaxBodyBytes} bytes.");
        }

        if (!PosReportParser.IsRoughlyWellFormed(body))
        {
            return Problem(
                HttpStatusCode.BadRequest,
                $"Report must start with '{PosReportParser.Prefix}' and have "
                + $"{PosReportParser.ExpectedParts} '/'-separated parts.");
        }

        // The flight ID needs the flight number, day, departure and destination,
        // so deriving it means parsing the message anyway. Doing the full parse
        // here means a 202 is a real promise: anything accepted will parse
        // downstream, and the caller learns about a bad report immediately
        // rather than by polling a status that never advances.
        var receivedAt = DateTimeOffset.UtcNow;

        if (!PosReportParser.TryParse(body, receivedAt, out var report, out var error))
        {
            context.Logger.LogWarning($"Rejected malformed report: {error}");
            return Problem(HttpStatusCode.BadRequest, error!);
        }

        var key = PosObjectKeys.RawKey(report!.FlightId, receivedAt);

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = body,
            ContentType = "text/plain",
        });

        context.Logger.LogInformation($"Received {report.FlightId}; stored at {key}");

        return Json(HttpStatusCode.Accepted, new
        {
            flightId = report.FlightId,
            status = "RECEIVED",
        });
    }

    private static string DecodeBody(APIGatewayProxyRequest request)
    {
        if (request.Body is null)
        {
            return string.Empty;
        }

        // API Gateway base64-encodes the body when the content type is treated
        // as binary, which depends on the caller's headers.
        return request.IsBase64Encoded
            ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body))
            : request.Body;
    }

    private static APIGatewayProxyResponse Problem(HttpStatusCode status, string message) =>
        Json(status, new { status = "REJECTED", message });

    private static APIGatewayProxyResponse Json(HttpStatusCode status, object body) =>
        new()
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = JsonSerializer.Serialize(body),
        };
}
