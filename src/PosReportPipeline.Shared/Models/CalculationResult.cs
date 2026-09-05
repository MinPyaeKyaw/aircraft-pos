using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosReportPipeline.Shared.Models;

/// <summary>
/// The inputs a calculation used, carried alongside its outputs so a stored
/// result is self-contained and reproducible without re-reading the report.
/// </summary>
public sealed record CalculationInput
{
    public required double CurrentLatitude { get; init; }
    public required double CurrentLongitude { get; init; }
    public required string Destination { get; init; }
    public required int GroundSpeedKnots { get; init; }
    public required double FuelOnBoardKg { get; init; }
    public required double FuelFlowKgPerHour { get; init; }
}

/// <summary>
/// The calculator's output. Serialised verbatim as the <c>results/</c> object.
/// </summary>
public sealed record CalculationResult
{
    public required string FlightId { get; init; }

    /// <summary>
    /// When the calculator produced this result -- NOT the report's own
    /// timestamp. Serialised to milliseconds, and used as the sort key of the
    /// results table.
    /// </summary>
    [JsonConverter(typeof(UtcMillisecondsJsonConverter))]
    public required DateTimeOffset Timestamp { get; init; }

    public required CalculationInput Input { get; init; }

    public required int RemainingFlightTimeMinutes { get; init; }

    public required double EstimatedFuelAtArrivalKg { get; init; }

    /// <summary>True when the aircraft is projected to arrive with negative fuel.</summary>
    public required bool LowFuelWarning { get; init; }
}

/// <summary>Writes a UTC instant as "2026-09-04T12:06:15.842Z".</summary>
public sealed class UtcMillisecondsJsonConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

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
/// The SQS message body between the parser and the calculator. Deliberately
/// carries only the two key attributes: the calculator re-reads the report from
/// the table so that a replayed or redriven message always works from the
/// stored record rather than a stale copy of it in the queue.
/// </summary>
public sealed record CalculationRequest
{
    public required string FlightId { get; init; }

    /// <summary>The report's timestamp, as the sort key string, e.g. "2026-09-04T12:05:00Z".</summary>
    public required string Timestamp { get; init; }
}
