namespace PosReportPipeline.Shared.Models;

/// <summary>Outcome classification for a calculation attempt.</summary>
public static class CalculationStatus
{
    public const string Ok = "OK";

    /// <summary>
    /// The destination ICAO is not in the catalog. A permanent failure:
    /// retrying cannot help, so the report is stored with null distances
    /// rather than discarded. The position itself is still real data.
    /// </summary>
    public const string UnknownDestination = "UNKNOWN_DESTINATION";
}

/// <summary>
/// An enriched position report, as stored in DynamoDB and returned by the
/// status API. Property names are DynamoDB attribute names.
/// </summary>
public sealed record CalculationResult
{
    public required string FlightId { get; init; }
    public required DateTimeOffset ReportedAt { get; init; }
    public required string Callsign { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required int AltitudeFeet { get; init; }
    public required string DestinationIcao { get; init; }

    /// <summary>Null when the destination is unknown.</summary>
    public string? DestinationName { get; init; }

    /// <summary>Null when the destination is unknown.</summary>
    public double? DistanceToDestinationKm { get; init; }

    /// <summary>Null when the destination is unknown.</summary>
    public double? DistanceToDestinationNm { get; init; }

    /// <summary>Degrees clockwise from true north. Null when the destination is unknown.</summary>
    public double? InitialBearingDegrees { get; init; }

    /// <summary>One of the <see cref="Models.CalculationStatus"/> constants.</summary>
    public required string CalculationStatus { get; init; }

    public required DateTimeOffset CalculatedAt { get; init; }

    public string? SourceKey { get; init; }
}
