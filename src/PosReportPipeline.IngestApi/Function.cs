using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace PosReportPipeline.IngestApi;

/// <summary>
/// Accepts a raw POS report and stores it verbatim in S3.
///
/// This function deliberately does not parse. Ingest stays fast, and a parser
/// bug can never lose data: the raw text is already durable and replayable.
/// </summary>
public class Function
{
    private const int MaxBodyBytes = 16 * 1024;
    private const string Header = "POS";

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    /// <summary>Used by the Lambda runtime.</summary>
    public Function()
        : this(new AmazonS3Client(),
               Environment.GetEnvironmentVariable("RAW_BUCKET")
               ?? throw new InvalidOperationException("RAW_BUCKET is not set."))
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

        var firstLine = body.Split('\n')[0].Trim('\r', ' ', '\t');
        if (!firstLine.Equals(Header, StringComparison.OrdinalIgnoreCase))
        {
            // Cheapest possible rejection of obvious garbage. Full validation
            // is the parser's job, downstream.
            return Problem(HttpStatusCode.BadRequest, $"First line must be '{Header}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var key = $"raw/{now:yyyy}/{now:MM}/{now:dd}/{Guid.NewGuid():N}.txt";

        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = body,
            ContentType = "text/plain",
        });

        context.Logger.LogInformation($"Stored report at s3://{_bucket}/{key}");

        return new APIGatewayProxyResponse
        {
            StatusCode = (int)HttpStatusCode.Accepted,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = JsonSerializer.Serialize(new
            {
                status = "accepted",
                key,
                receivedAt = now.ToString("o"),
            }),
        };
    }

    private static string DecodeBody(APIGatewayProxyRequest request)
    {
        if (request.Body is null)
        {
            return string.Empty;
        }

        return request.IsBase64Encoded
            ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body))
            : request.Body;
    }

    private static APIGatewayProxyResponse Problem(HttpStatusCode status, string message) =>
        new()
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = JsonSerializer.Serialize(new { status = "rejected", message }),
        };
}
