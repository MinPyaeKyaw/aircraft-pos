using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

public class FlightIdTests
{
    [Fact]
    public void BuildFlightId_CombinesCallsignAndUtcDate()
    {
        var at = new DateTimeOffset(2026, 9, 5, 14, 20, 0, TimeSpan.Zero);
        Assert.Equal("ABC123-20260905", PosReportParser.BuildFlightId("ABC123", at));
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("AbC123")]
    [InlineData("  ABC123  ")]
    public void BuildFlightId_NormalisesCallsignCasingAndWhitespace(string callsign)
    {
        var at = new DateTimeOffset(2026, 9, 5, 14, 20, 0, TimeSpan.Zero);
        Assert.Equal("ABC123-20260905", PosReportParser.BuildFlightId(callsign, at));
    }

    [Fact]
    public void BuildFlightId_UsesUtcDateNotLocalDate()
    {
        // 2026-09-05T23:30-07:00 is 2026-09-06T06:30Z. The UTC date wins.
        var at = new DateTimeOffset(2026, 9, 5, 23, 30, 0, TimeSpan.FromHours(-7));
        Assert.Equal("ABC123-20260906", PosReportParser.BuildFlightId("ABC123", at));
    }

    [Fact]
    public void BuildFlightId_JustBeforeUtcMidnight_UsesThatDay()
    {
        var at = new DateTimeOffset(2026, 9, 5, 23, 59, 59, TimeSpan.Zero);
        Assert.Equal("ABC123-20260905", PosReportParser.BuildFlightId("ABC123", at));
    }

    [Fact]
    public void BuildFlightId_AtUtcMidnight_RollsToTheNextDay()
    {
        // Documents the accepted consequence: a flight crossing UTC midnight
        // splits into two flight IDs. See the design doc, "Flight ID".
        var at = new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal("ABC123-20260906", PosReportParser.BuildFlightId("ABC123", at));
    }

    [Fact]
    public void BuildFlightId_PadsSingleDigitMonthsAndDays()
    {
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        Assert.Equal("XY9-20260102", PosReportParser.BuildFlightId("XY9", at));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFlightId_BlankCallsign_Throws(string? callsign)
    {
        Assert.Throws<ArgumentException>(
            () => PosReportParser.BuildFlightId(callsign!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BuildFlightId_MatchesTheIdProducedByParse()
    {
        var raw = """
            POS
            FLT/xyz789
            DT/2026-12-31T23:59:00Z
            PSN/S3350.0 E15110.0
            ALT/FL380
            DEST/YSSY
            """;

        var report = PosReportParser.Parse(raw);
        Assert.Equal(PosReportParser.BuildFlightId("XYZ789", report.ReportedAtUtc), report.FlightId);
        Assert.Equal("XYZ789-20261231", report.FlightId);
    }
}
