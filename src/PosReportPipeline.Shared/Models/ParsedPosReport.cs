using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosReportPipeline.Shared.Models;

/// <summary>
/// The structured form of a POS report. Serialised verbatim as the
/// <c>attachment/</c> object, so these property names are wire format and must
/// not be renamed without migrating stored attachments.
/// </summary>
public sealed record ParsedPosReport
{
    /// <summary>e.g. "UL20420260904RGNBKK".</summary>
    public required string FlightId { get; init; }

    /// <summary>IATA airline code plus flight number, e.g. "UL204".</summary>
    public required string FlightNumber { get; init; }

    public required string Departure { get; init; }

    public required string Destination { get; init; }

    /// <summary>
    /// Report time, to the minute. Serialised as "2026-09-04T12:05:00Z" and
    /// used verbatim as the DynamoDB sort key.
    /// </summary>
    [JsonConverter(typeof(UtcSecondsJsonConverter))]
    public required DateTimeOffset Timestamp { get; init; }

    public required double Latitude { get; init; }

    public required double Longitude { get; init; }

    public required int GroundSpeedKnots { get; init; }

    public required double FuelOnBoardKg { get; init; }

    public required double FuelFlowKgPerHour { get; init; }
}

/// <summary>
/// Writes a UTC instant as "2026-09-04T12:05:00Z".
///
/// The default DateTimeOffset converter emits a "+00:00" offset, which would
/// not match the attachment format the assessment specifies -- and because the
/// same string is the DynamoDB sort key, a mismatch would silently break
/// lookups rather than fail loudly.
/// </summary>
public sealed class UtcSecondsJsonConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(
            reader.GetString() ?? throw new JsonException("Expected a timestamp string."),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>
/// The single JSON configuration every Lambda uses, so the attachment written
/// by one function is always readable by the next.
/// </summary>
public static class PosJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
