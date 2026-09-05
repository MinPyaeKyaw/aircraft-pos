using System.Globalization;
using System.Text.RegularExpressions;
using PosReportPipeline.Shared.Models;

namespace PosReportPipeline.Shared.Parsing;

/// <summary>
/// Raised when a report cannot be parsed. Always a permanent failure: the same
/// input will never parse, so callers must not retry.
/// </summary>
public sealed class PosReportParseException : Exception
{
    public PosReportParseException(string message) : base(message) { }
}

/// <summary>
/// Parses ACARS-style POS reports. Format:
/// <code>
/// POS
/// FLT/ABC123
/// DT/2026-09-05T14:20:00Z
/// PSN/N3722.5 W12205.8
/// ALT/FL350
/// DEST/KLAX
/// </code>
/// Line one must be POS. Every other line is KEY/VALUE split on the first
/// slash only. Unknown keys are ignored so the format can grow without
/// breaking deployed parsers.
/// </summary>
public static class PosReportParser
{
    private const string Header = "POS";

    // DDMM.M: two degree digits for latitude, three for longitude, minutes
    // with optional decimals. The hemisphere letter leads.
    private static readonly Regex LatitudePattern =
        new(@"^([NS])(\d{2})(\d{2}(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LongitudePattern =
        new(@"^([EW])(\d{3})(\d{2}(?:\.\d+)?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FlightLevelPattern =
        new(@"^FL(\d{2,3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FeetPattern =
        new(@"^(\d{1,6})FT$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IcaoPattern =
        new(@"^[A-Z]{4}$", RegexOptions.Compiled);

    /// <summary>Parses a report, or throws <see cref="PosReportParseException"/>.</summary>
    public static ParsedPosReport Parse(string raw, string? sourceKey = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new PosReportParseException("Report is empty; expected a POS header.");
        }

        var lines = raw
            .Split('\n')
            .Select(line => line.Trim('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToArray();

        if (lines.Length == 0 || !lines[0].Equals(Header, StringComparison.OrdinalIgnoreCase))
        {
            var actual = lines.Length == 0 ? string.Empty : lines[0];
            throw new PosReportParseException(
                $"First line must be '{Header}' but was '{actual}'.");
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var slash = line.IndexOf('/');
            if (slash <= 0)
            {
                continue; // Not a KEY/VALUE line; ignore rather than fail.
            }

            var key = line[..slash].Trim();
            var value = line[(slash + 1)..].Trim();

            // First occurrence wins, so a duplicated key cannot silently
            // overwrite the value an earlier line already established.
            fields.TryAdd(key, value);
        }

        var callsign = Required(fields, "FLT").ToUpperInvariant();
        var reportedAt = ParseTimestamp(Required(fields, "DT"));
        var (latitude, longitude) = ParsePosition(Required(fields, "PSN"));
        var altitudeFeet = ParseAltitude(Required(fields, "ALT"));
        var destination = ParseDestination(Required(fields, "DEST"));

        return new ParsedPosReport
        {
            FlightId = BuildFlightId(callsign, reportedAt),
            Callsign = callsign,
            ReportedAtUtc = reportedAt,
            Latitude = latitude,
            Longitude = longitude,
            AltitudeFeet = altitudeFeet,
            DestinationIcao = destination,
            SourceKey = sourceKey,
        };
    }

    /// <summary>Non-throwing form of <see cref="Parse"/>.</summary>
    public static bool TryParse(
        string raw, string? sourceKey, out ParsedPosReport? report, out string? error)
    {
        try
        {
            report = Parse(raw, sourceKey);
            error = null;
            return true;
        }
        catch (PosReportParseException ex)
        {
            report = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Builds the flight ID: uppercased callsign plus the UTC date of the
    /// report. A flight crossing UTC midnight therefore gets two IDs; see the
    /// design doc for why that trade is accepted.
    /// </summary>
    public static string BuildFlightId(string callsign, DateTimeOffset reportedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(callsign))
        {
            throw new ArgumentException("Callsign must not be blank.", nameof(callsign));
        }

        var date = reportedAtUtc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"{callsign.Trim().ToUpperInvariant()}-{date}";
    }

    private static string Required(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
        {
            throw new PosReportParseException($"Required field '{key}' is missing or empty.");
        }

        return value;
    }

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new PosReportParseException($"Field 'DT' is not a valid ISO-8601 instant: '{value}'.");
        }

        // AdjustToUniversal converts rather than rejects, so check the raw text:
        // an offset that is not Z means the sender is not reporting UTC, and we
        // should not guess on their behalf.
        var trimmed = value.Trim();
        var isUtc = trimmed.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                 || trimmed.EndsWith("+00:00", StringComparison.Ordinal)
                 || trimmed.EndsWith("-00:00", StringComparison.Ordinal);

        if (!isUtc)
        {
            throw new PosReportParseException(
                $"Field 'DT' must be UTC and end with 'Z' but was '{value}'.");
        }

        return parsed.ToUniversalTime();
    }

    private static (double Latitude, double Longitude) ParsePosition(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new PosReportParseException($"Field 'PSN' must be '<lat> <lon>' but was '{value}'.");
        }

        var latitude = ParseCoordinate(LatitudePattern, parts[0], 90d, 'S', "latitude");
        var longitude = ParseCoordinate(LongitudePattern, parts[1], 180d, 'W', "longitude");

        return (latitude, longitude);
    }

    /// <summary>Converts one DDMM.M coordinate to signed decimal degrees.</summary>
    private static double ParseCoordinate(
        Regex pattern, string value, double limit, char negativeHemisphere, string label)
    {
        var match = pattern.Match(value);
        if (!match.Success)
        {
            throw new PosReportParseException(
                $"Field 'PSN' has a malformed {label}: '{value}'. Expected DDMM.M form, e.g. N3722.5.");
        }

        var hemisphere = char.ToUpperInvariant(match.Groups[1].Value[0]);
        var degrees = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minutes = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (minutes >= 60d)
        {
            throw new PosReportParseException(
                $"Field 'PSN' has {label} minutes of {minutes}, which must be less than 60.");
        }

        var decimalDegrees = degrees + (minutes / 60d);
        if (decimalDegrees > limit)
        {
            throw new PosReportParseException(
                $"Field 'PSN' has {label} {decimalDegrees:F4}, outside the valid range of +/-{limit}.");
        }

        return hemisphere == negativeHemisphere ? -decimalDegrees : decimalDegrees;
    }

    private static int ParseAltitude(string value)
    {
        var flightLevel = FlightLevelPattern.Match(value);
        if (flightLevel.Success)
        {
            return int.Parse(flightLevel.Groups[1].Value, CultureInfo.InvariantCulture) * 100;
        }

        var feet = FeetPattern.Match(value);
        if (feet.Success)
        {
            return int.Parse(feet.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        throw new PosReportParseException(
            $"Field 'ALT' must be a flight level (FL350) or feet (2500FT) but was '{value}'.");
    }

    private static string ParseDestination(string value)
    {
        var destination = value.Trim().ToUpperInvariant();
        if (!IcaoPattern.IsMatch(destination))
        {
            throw new PosReportParseException(
                $"Field 'DEST' must be a four-letter ICAO code but was '{value}'.");
        }

        return destination;
    }
}
