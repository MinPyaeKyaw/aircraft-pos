using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

public class PosReportParserTests
{
    private const string ValidReport = """
        POS
        FLT/ABC123
        DT/2026-09-05T14:20:00Z
        PSN/N3722.5 W12205.8
        ALT/FL350
        DEST/KLAX
        """;

    [Fact]
    public void Parse_ValidReport_ExtractsEveryField()
    {
        var report = PosReportParser.Parse(ValidReport);

        Assert.Equal("ABC123", report.Callsign);
        Assert.Equal("ABC123-20260905", report.FlightId);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 14, 20, 0, TimeSpan.Zero), report.ReportedAtUtc);
        Assert.Equal(37.375, report.Latitude, 6);
        Assert.Equal(-122.096667, report.Longitude, 6);
        Assert.Equal(35000, report.AltitudeFeet);
        Assert.Equal("KLAX", report.DestinationIcao);
    }

    [Fact]
    public void Parse_PassesThroughSourceKey()
    {
        var report = PosReportParser.Parse(ValidReport, "raw/2026/09/05/abc.txt");
        Assert.Equal("raw/2026/09/05/abc.txt", report.SourceKey);
    }

    // N3722.5  => 37 + 22.5/60 = 37.375
    // E12205.8 => 122 + 5.8/60 = 122.096667
    [Theory]
    [InlineData("N3722.5 E12205.8", 37.375, 122.096667)]
    [InlineData("S3722.5 E12205.8", -37.375, 122.096667)]
    [InlineData("N3722.5 W12205.8", 37.375, -122.096667)]
    [InlineData("S3722.5 W12205.8", -37.375, -122.096667)]
    public void Parse_ConvertsAllFourHemispheres(string psn, double expectedLat, double expectedLon)
    {
        var report = PosReportParser.Parse(ReportWith("PSN", psn));
        Assert.Equal(expectedLat, report.Latitude, 6);
        Assert.Equal(expectedLon, report.Longitude, 6);
    }

    [Fact]
    public void Parse_ZeroMinutes_IsWholeDegrees()
    {
        var report = PosReportParser.Parse(ReportWith("PSN", "N0000.0 E00000.0"));
        Assert.Equal(0d, report.Latitude, 6);
        Assert.Equal(0d, report.Longitude, 6);
    }

    [Theory]
    [InlineData("FL350", 35000)]
    [InlineData("FL010", 1000)]
    [InlineData("2500FT", 2500)]
    [InlineData("500FT", 500)]
    public void Parse_AcceptsBothAltitudeForms(string alt, int expectedFeet)
    {
        Assert.Equal(expectedFeet, PosReportParser.Parse(ReportWith("ALT", alt)).AltitudeFeet);
    }

    [Fact]
    public void Parse_IgnoresUnknownKeys()
    {
        var withExtra = ValidReport + "\nSPD/450\nFOB/12.3";
        Assert.Equal("ABC123", PosReportParser.Parse(withExtra).Callsign);
    }

    [Fact]
    public void Parse_IgnoresBlankLines()
    {
        var withBlanks = ValidReport.Replace("\n", "\n\n");
        Assert.Equal("ABC123", PosReportParser.Parse(withBlanks).Callsign);
    }

    [Fact]
    public void Parse_AcceptsWindowsLineEndings()
    {
        Assert.Equal("ABC123", PosReportParser.Parse(ValidReport.Replace("\n", "\r\n")).Callsign);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NOTPOS\nFLT/ABC123")]
    [InlineData("ACK\nFLT/ABC123")]
    public void Parse_WrongOrMissingHeader_Throws(string raw)
    {
        var ex = Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(raw));
        Assert.Contains("POS", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_HeaderIsCaseInsensitiveAndTrimmed()
    {
        var raw = "  pos  \n" + string.Join("\n", ValidReport.Split('\n').Skip(1));
        Assert.Equal("ABC123", PosReportParser.Parse(raw).Callsign);
    }

    [Theory]
    [InlineData("FLT")]
    [InlineData("DT")]
    [InlineData("PSN")]
    [InlineData("ALT")]
    [InlineData("DEST")]
    public void Parse_MissingRequiredField_ThrowsNamingTheField(string field)
    {
        var raw = string.Join("\n",
            ValidReport.Split('\n').Where(l => !l.TrimStart().StartsWith(field + "/", StringComparison.Ordinal)));

        var ex = Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(raw));
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("N3760.0 W12205.8")]   // latitude minutes >= 60
    [InlineData("N3722.5 W12260.0")]   // longitude minutes >= 60
    [InlineData("N9122.5 W12205.8")]   // latitude degrees out of range
    [InlineData("N3722.5 W18105.8")]   // longitude degrees out of range
    [InlineData("X3722.5 W12205.8")]   // bad hemisphere letter
    [InlineData("N3722.5")]            // longitude missing
    [InlineData("N372 W12205.8")]      // malformed latitude
    [InlineData("nonsense")]
    public void Parse_InvalidPosition_Throws(string psn)
    {
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(ReportWith("PSN", psn)));
    }

    [Theory]
    [InlineData("FL")]
    [InlineData("FLABC")]
    [InlineData("350")]
    [InlineData("-100FT")]
    public void Parse_InvalidAltitude_Throws(string alt)
    {
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(ReportWith("ALT", alt)));
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-05T14:20:00Z")]
    public void Parse_InvalidTimestamp_Throws(string dt)
    {
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(ReportWith("DT", dt)));
    }

    [Fact]
    public void Parse_NonUtcTimestamp_Throws()
    {
        var ex = Assert.Throws<PosReportParseException>(
            () => PosReportParser.Parse(ReportWith("DT", "2026-09-05T14:20:00+07:00")));
        Assert.Contains("UTC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("KLA")]
    [InlineData("KLAXX")]
    [InlineData("KL4X")]
    public void Parse_MalformedDestination_Throws(string dest)
    {
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(ReportWith("DEST", dest)));
    }

    [Fact]
    public void Parse_UnknownButWellFormedDestination_Succeeds()
    {
        // Catalog membership is the calculator's problem, not the parser's.
        Assert.Equal("ZZZZ", PosReportParser.Parse(ReportWith("DEST", "ZZZZ")).DestinationIcao);
    }

    [Fact]
    public void Parse_UppercasesCallsignAndDestination()
    {
        var raw = ReportWith("FLT", "abc123").Replace("DEST/KLAX", "DEST/klax");
        var report = PosReportParser.Parse(raw);
        Assert.Equal("ABC123", report.Callsign);
        Assert.Equal("KLAX", report.DestinationIcao);
    }

    [Fact]
    public void Parse_DuplicateKey_KeepsTheFirstOccurrence()
    {
        var raw = ValidReport + "\nFLT/HIJACKED";
        Assert.Equal("ABC123", PosReportParser.Parse(raw).Callsign);
    }

    [Fact]
    public void TryParse_ValidReport_ReturnsTrueWithNoError()
    {
        Assert.True(PosReportParser.TryParse(ValidReport, null, out var report, out var error));
        Assert.NotNull(report);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_InvalidReport_ReturnsFalseWithReason()
    {
        Assert.False(PosReportParser.TryParse("garbage", null, out var report, out var error));
        Assert.Null(report);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>Returns the valid report with one field's value replaced.</summary>
    private static string ReportWith(string key, string value) =>
        string.Join("\n", ValidReport.Split('\n')
            .Select(line => line.TrimStart().StartsWith(key + "/", StringComparison.Ordinal)
                ? key + "/" + value
                : line));
}
