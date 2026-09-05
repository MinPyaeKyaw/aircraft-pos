namespace PosReportPipeline.Shared.Models;

/// <summary>
/// A position report after parsing. This is the SQS message payload between
/// the parser and the calculator, so its property names are wire format.
/// </summary>
public sealed record ParsedPosReport
{
    /// <summary>"{CALLSIGN}-{yyyyMMdd}", e.g. "ABC123-20260905".</summary>
    public required string FlightId { get; init; }

    /// <summary>Uppercased callsign from the FLT field, e.g. "ABC123".</summary>
    public required string Callsign { get; init; }

    /// <summary>Instant of the report. Always UTC.</summary>
    public required DateTimeOffset ReportedAtUtc { get; init; }

    /// <summary>Decimal degrees, north positive. Range [-90, 90].</summary>
    public required double Latitude { get; init; }

    /// <summary>Decimal degrees, east positive. Range [-180, 180].</summary>
    public required double Longitude { get; init; }

    public required int AltitudeFeet { get; init; }

    /// <summary>Uppercased four-letter ICAO code from the DEST field.</summary>
    public required string DestinationIcao { get; init; }

    /// <summary>S3 key the report was read from. Null when parsed outside S3.</summary>
    public string? SourceKey { get; init; }
}
