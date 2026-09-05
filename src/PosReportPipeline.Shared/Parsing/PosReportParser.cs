using System.Globalization;
using System.Text.RegularExpressions;
using PosReportPipeline.Shared.Models;

namespace PosReportPipeline.Shared.Parsing;

/// <summary>
/// Raised when a POS report cannot be parsed. Always a permanent failure: the
/// same input will never parse, so callers must not retry it.
/// </summary>
public sealed class PosReportParseException : Exception
{
    public PosReportParseException(string message) : base(message) { }
}

/// <summary>
/// Parses POS reports of the form:
/// <code>
/// POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800
/// </code>
/// which is
/// <c>POS/{flightNumber}.FR {departure}/TO {destination}/{ddHHmm}/{position}/
/// {groundSpeedKnots}/{fuelOnBoardKg}/{fuelFlowKgPerHour}</c>.
///
/// A report carries only a day-of-month and a time, never a year or month.
/// Both are therefore supplied by the caller as <c>receivedAtUtc</c> rather
/// than read from the clock inside this class, so that parsing is a pure
/// function: the same raw text and the same receipt time always produce the
/// same flight ID. See <see cref="PosObjectKeys"/> for how the parser Lambda
/// recovers the original receipt time.
/// </summary>
public static class PosReportParser
{
    public const string Prefix = "POS/";

    /// <summary>POS, flight/departure, destination, ddHHmm, position, speed, fuel, flow.</summary>
    public const int ExpectedParts = 8;

    /// <summary>Decimal places kept for latitude and longitude.</summary>
    public const int DecimalPlaces = 4;

    // "N1642.3E09612.5" -- hemisphere letters lead, per the worked example.
    private static readonly Regex PositionHemisphereFirst = new(
        @"^([NS])(\d{2})(\d{2}(?:\.\d+)?)([EW])(\d{3})(\d{2}(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "1642.3N09612.5E" -- hemisphere letters trail. The format line in the
    // brief is written this way even though its example is not, so both are
    // accepted rather than guessing which one real traffic uses.
    private static readonly Regex PositionHemisphereLast = new(
        @"^(\d{2})(\d{2}(?:\.\d+)?)([NS])(\d{3})(\d{2}(?:\.\d+)?)([EW])$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "UL204.FR RGN"
    private static readonly Regex FlightAndDeparture = new(
        @"^([A-Z0-9]{2}\d{1,4})\.FR\s+([A-Z]{3})$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "TO BKK"
    private static readonly Regex DestinationPattern = new(
        @"^TO\s+([A-Z]{3})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "041205"
    private static readonly Regex DayHourMinutePattern = new(
        @"^(\d{2})(\d{2})(\d{2})$", RegexOptions.Compiled);

    /// <summary>
    /// The cheap structural check the ingest API runs before accepting a
    /// request: is this plausibly a POS report at all?
    /// </summary>
    public static bool IsRoughlyWellFormed(string? raw) =>
        !string.IsNullOrWhiteSpace(raw)
        && raw.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
        && raw.Trim().Split('/').Length == ExpectedParts;

    /// <summary>
    /// Parses a report. <paramref name="receivedAtUtc"/> supplies the year and
    /// month the message itself omits.
    /// </summary>
    /// <exception cref="PosReportParseException">The report is malformed.</exception>
    public static ParsedPosReport Parse(string raw, DateTimeOffset receivedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new PosReportParseException("Report is empty.");
        }

        var parts = raw.Trim().Split('/');
        if (parts.Length != ExpectedParts)
        {
            throw new PosReportParseException(
                $"Expected {ExpectedParts} '/'-separated parts but found {parts.Length}.");
        }

        if (!parts[0].Trim().Equals("POS", StringComparison.OrdinalIgnoreCase))
        {
            throw new PosReportParseException($"Report must start with 'POS/' but began '{parts[0]}'.");
        }

        var (flightNumber, departure) = ParseFlightAndDeparture(parts[1]);
        var destination = ParseDestination(parts[2]);
        var (day, hour, minute) = ParseDayHourMinute(parts[3]);
        var (latitude, longitude) = ParsePosition(parts[4]);
        var groundSpeedKnots = ParseGroundSpeed(parts[5]);
        var fuelOnBoardKg = ParseNonNegative(parts[6], "fuel on board");
        var fuelFlowKgPerHour = ParseNonNegative(parts[7], "fuel flow");

        var timestamp = BuildTimestamp(day, hour, minute, receivedAtUtc);

        return new ParsedPosReport
        {
            FlightId = BuildFlightId(flightNumber, departure, destination, timestamp),
            FlightNumber = flightNumber,
            Departure = departure,
            Destination = destination,
            Timestamp = timestamp,
            Latitude = latitude,
            Longitude = longitude,
            GroundSpeedKnots = groundSpeedKnots,
            FuelOnBoardKg = fuelOnBoardKg,
            FuelFlowKgPerHour = fuelFlowKgPerHour,
        };
    }

    /// <summary>Non-throwing form of <see cref="Parse"/>.</summary>
    public static bool TryParse(
        string raw, DateTimeOffset receivedAtUtc, out ParsedPosReport? report, out string? error)
    {
        try
        {
            report = Parse(raw, receivedAtUtc);
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
    /// Builds the report timestamp. The message supplies day, hour and minute;
    /// the year and month come from when the report was received.
    /// </summary>
    public static DateTimeOffset BuildTimestamp(
        int day, int hour, int minute, DateTimeOffset receivedAtUtc)
    {
        var received = receivedAtUtc.ToUniversalTime();
        var daysInMonth = DateTime.DaysInMonth(received.Year, received.Month);

        if (day < 1 || day > daysInMonth)
        {
            throw new PosReportParseException(
                $"Day {day:00} does not exist in {received:yyyy-MM}, the month the report was received.");
        }

        return new DateTimeOffset(
            received.Year, received.Month, day, hour, minute, 0, TimeSpan.Zero);
    }

    /// <summary>
    /// Builds the flight ID: flight number, flight date, departure and
    /// destination concatenated with no separators, e.g.
    /// "UL204" + "20260904" + "RGN" + "BKK" = "UL20420260904RGNBKK".
    /// </summary>
    public static string BuildFlightId(
        string flightNumber, string departure, string destination, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(flightNumber))
        {
            throw new ArgumentException("Flight number must not be blank.", nameof(flightNumber));
        }

        var date = timestamp.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        return string.Concat(
            flightNumber.Trim().ToUpperInvariant(),
            date,
            departure.Trim().ToUpperInvariant(),
            destination.Trim().ToUpperInvariant());
    }

    private static (string FlightNumber, string Departure) ParseFlightAndDeparture(string part)
    {
        var match = FlightAndDeparture.Match(part.Trim());
        if (!match.Success)
        {
            throw new PosReportParseException(
                $"Expected '<flightNumber>.FR <departure>' but found '{part}'.");
        }

        return (match.Groups[1].Value.ToUpperInvariant(), match.Groups[2].Value.ToUpperInvariant());
    }

    private static string ParseDestination(string part)
    {
        var match = DestinationPattern.Match(part.Trim());
        if (!match.Success)
        {
            throw new PosReportParseException($"Expected 'TO <destination>' but found '{part}'.");
        }

        return match.Groups[1].Value.ToUpperInvariant();
    }

    private static (int Day, int Hour, int Minute) ParseDayHourMinute(string part)
    {
        var match = DayHourMinutePattern.Match(part.Trim());
        if (!match.Success)
        {
            throw new PosReportParseException(
                $"Expected a 6-digit ddHHmm field but found '{part}'.");
        }

        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var hour = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        if (hour > 23 || minute > 59)
        {
            throw new PosReportParseException($"'{part}' is not a valid ddHHmm time.");
        }

        return (day, hour, minute);
    }

    /// <summary>
    /// Converts the combined position block to decimal degrees. Latitude has
    /// two degree digits and longitude three, which is the only thing
    /// separating them -- there is no delimiter.
    /// </summary>
    private static (double Latitude, double Longitude) ParsePosition(string part)
    {
        var value = part.Trim();

        var match = PositionHemisphereFirst.Match(value);
        var hemisphereFirst = match.Success;

        if (!hemisphereFirst)
        {
            match = PositionHemisphereLast.Match(value);
        }

        if (!match.Success)
        {
            throw new PosReportParseException(
                $"Expected a position like 'N1642.3E09612.5' but found '{value}'.");
        }

        // Group order differs between the two forms; normalise it here.
        var (latHemisphere, latDegrees, latMinutes, lonHemisphere, lonDegrees, lonMinutes) =
            hemisphereFirst
                ? (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value,
                   match.Groups[4].Value, match.Groups[5].Value, match.Groups[6].Value)
                : (match.Groups[3].Value, match.Groups[1].Value, match.Groups[2].Value,
                   match.Groups[6].Value, match.Groups[4].Value, match.Groups[5].Value);

        var latitude = ToDecimalDegrees(latHemisphere, latDegrees, latMinutes, 90, 'S', "latitude");
        var longitude = ToDecimalDegrees(lonHemisphere, lonDegrees, lonMinutes, 180, 'W', "longitude");

        // Rounded to the precision the brief's attachment example uses. Four
        // decimal places is about 11 m -- far finer than the 0.1-minute
        // (~185 m) resolution the report itself carries, so nothing real is
        // lost, and the stored JSON matches the specified format exactly.
        return (Math.Round(latitude, DecimalPlaces), Math.Round(longitude, DecimalPlaces));
    }

    /// <summary>degrees + minutes / 60, negated in the southern and western hemispheres.</summary>
    private static double ToDecimalDegrees(
        string hemisphere, string degreesText, string minutesText,
        double limit, char negativeHemisphere, string label)
    {
        var degrees = double.Parse(degreesText, CultureInfo.InvariantCulture);
        var minutes = double.Parse(minutesText, CultureInfo.InvariantCulture);

        if (minutes >= 60d)
        {
            throw new PosReportParseException(
                $"Position has {label} minutes of {minutes}, which must be under 60.");
        }

        var value = degrees + (minutes / 60d);
        if (value > limit)
        {
            throw new PosReportParseException(
                $"Position has {label} {value:F4}, outside the valid range of +/-{limit}.");
        }

        return char.ToUpperInvariant(hemisphere[0]) == negativeHemisphere ? -value : value;
    }

    private static int ParseGroundSpeed(string part)
    {
        if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var knots))
        {
            throw new PosReportParseException($"Ground speed '{part}' is not an integer.");
        }

        // The calculator divides by this. A zero would produce an infinite
        // remaining flight time, so it is rejected here rather than allowed to
        // become a poison message on the queue.
        if (knots <= 0)
        {
            throw new PosReportParseException(
                $"Ground speed must be positive but was {knots}; remaining flight time would be undefined.");
        }

        return knots;
    }

    private static double ParseNonNegative(string part, string label)
    {
        if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new PosReportParseException($"Value for {label} ('{part}') is not a number.");
        }

        if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new PosReportParseException($"Value for {label} must be a non-negative number but was '{part}'.");
        }

        return value;
    }
}

/// <summary>
/// Builds and reads the three S3 key formats, so the shape of a key is defined
/// once instead of being string-concatenated in four Lambdas.
///
/// The raw key carries the receipt time, which is what lets the parser Lambda
/// recover the year and month the ingest API used. Reading the clock again in
/// the parser would produce a different flight ID for any report that crosses
/// a month boundary between ingest and parse, and the mismatch would be silent.
/// </summary>
public static class PosObjectKeys
{
    public const string RawPrefix = "pos/";
    public const string AttachmentPrefix = "attachment/";
    public const string ResultsPrefix = "results/";

    /// <summary>pos/UL20420260904RGNBKK-2026-09-04T15:12:30.123Z</summary>
    public static string RawKey(string flightId, DateTimeOffset receivedAtUtc) =>
        $"{RawPrefix}{flightId}-{FormatMilliseconds(receivedAtUtc)}";

    /// <summary>attachment/UL20420260904RGNBKK-2026-09-04T12:05:00Z.json</summary>
    public static string AttachmentKey(string flightId, DateTimeOffset reportTimestamp) =>
        $"{AttachmentPrefix}{flightId}-{FormatSeconds(reportTimestamp)}.json";

    /// <summary>results/UL20420260904RGNBKK-2026-09-04T12:06:15.842Z.json</summary>
    public static string ResultKey(string flightId, DateTimeOffset calculatedAtUtc) =>
        $"{ResultsPrefix}{flightId}-{FormatMilliseconds(calculatedAtUtc)}.json";

    /// <summary>
    /// Recovers the flight ID and receipt time from a raw key. A flight ID is
    /// alphanumeric, so the first '-' after the prefix is unambiguously the
    /// separator even though the timestamp contains more of them.
    /// </summary>
    public static bool TryParseRawKey(string key, out string flightId, out DateTimeOffset receivedAtUtc)
    {
        flightId = string.Empty;
        receivedAtUtc = default;

        if (string.IsNullOrEmpty(key) || !key.StartsWith(RawPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = key[RawPrefix.Length..];
        var separator = remainder.IndexOf('-');
        if (separator <= 0)
        {
            return false;
        }

        var candidateId = remainder[..separator];
        var candidateTimestamp = remainder[(separator + 1)..];

        if (!DateTimeOffset.TryParseExact(
                candidateTimestamp,
                UtcMillisecondsJsonConverter.Format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        flightId = candidateId;
        receivedAtUtc = parsed;
        return true;
    }

    public static string FormatSeconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(UtcSecondsJsonConverter.Format, CultureInfo.InvariantCulture);

    public static string FormatMilliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(UtcMillisecondsJsonConverter.Format, CultureInfo.InvariantCulture);
}
