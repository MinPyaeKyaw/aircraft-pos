using System.Text.Json;
using PosReportPipeline.Shared.Models;
using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

public class PosReportParserTests
{
    /// <summary>The example message from the brief.</summary>
    private const string Example = "POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800";

    /// <summary>The moment the brief says the API receives it.</summary>
    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 4, 15, 12, 30, 123, TimeSpan.Zero);

    [Fact]
    public void Parse_TheBriefsExample_ProducesEveryFieldTheBriefSpecifies()
    {
        var report = PosReportParser.Parse(Example, ReceivedAt);

        Assert.Equal("UL20420260904RGNBKK", report.FlightId);
        Assert.Equal("UL204", report.FlightNumber);
        Assert.Equal("RGN", report.Departure);
        Assert.Equal("BKK", report.Destination);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 12, 5, 0, TimeSpan.Zero), report.Timestamp);
        Assert.Equal(16.705, report.Latitude, 4);
        Assert.Equal(96.2083, report.Longitude, 4);
        Assert.Equal(450, report.GroundSpeedKnots);
        Assert.Equal(12500d, report.FuelOnBoardKg);
        Assert.Equal(2800d, report.FuelFlowKgPerHour);
    }

    [Fact]
    public void Parse_TheBriefsExample_SerialisesToTheAttachmentJsonInTheBrief()
    {
        var report = PosReportParser.Parse(Example, ReceivedAt);
        var json = JsonSerializer.Serialize(report, PosJson.Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("UL20420260904RGNBKK", root.GetProperty("flightId").GetString());
        Assert.Equal("UL204", root.GetProperty("flightNumber").GetString());
        Assert.Equal("RGN", root.GetProperty("departure").GetString());
        Assert.Equal("BKK", root.GetProperty("destination").GetString());

        // Must be exactly this shape: the same string is the DynamoDB sort key,
        // so a stray "+00:00" would silently break every lookup.
        Assert.Equal("2026-09-04T12:05:00Z", root.GetProperty("timestamp").GetString());

        Assert.Equal(16.705, root.GetProperty("latitude").GetDouble(), 4);
        Assert.Equal(96.2083, root.GetProperty("longitude").GetDouble(), 4);
        Assert.Equal(450, root.GetProperty("groundSpeedKnots").GetInt32());
        Assert.Equal(12500d, root.GetProperty("fuelOnBoardKg").GetDouble());
        Assert.Equal(2800d, root.GetProperty("fuelFlowKgPerHour").GetDouble());
    }

    // degrees + minutes / 60, negative south and west.
    [Theory]
    [InlineData("N1642.3E09612.5", 16.705, 96.2083)]
    [InlineData("S1642.3E09612.5", -16.705, 96.2083)]
    [InlineData("N1642.3W09612.5", 16.705, -96.2083)]
    [InlineData("S1642.3W09612.5", -16.705, -96.2083)]
    [InlineData("N0000.0E00000.0", 0, 0)]
    [InlineData("S3356.4E15110.5", -33.94, 151.175)]
    public void Parse_ConvertsDegreesAndMinutesInEveryHemisphere(
        string position, double expectedLat, double expectedLon)
    {
        var report = PosReportParser.Parse(WithPart(4, position), ReceivedAt);
        Assert.Equal(expectedLat, report.Latitude, 6);
        Assert.Equal(expectedLon, report.Longitude, 6);
    }

    [Fact]
    public void Parse_AlsoAcceptsHemisphereLettersAfterTheDigits()
    {
        // The brief's format line reads {lat}{N|S}{lon}{E|W} while its example
        // puts the letters first. Both are accepted rather than guessing which
        // one real traffic uses.
        var report = PosReportParser.Parse(WithPart(4, "1642.3N09612.5E"), ReceivedAt);
        Assert.Equal(16.705, report.Latitude, 6);
        Assert.Equal(96.2083, report.Longitude, 6);
    }

    [Fact]
    public void Parse_SplitsLatitudeAndLongitudeByDigitCountNotByAnySeparator()
    {
        // Two degree digits for latitude, three for longitude. A greedy split
        // would read "N16" then "42.3E096..." and quietly produce nonsense.
        var report = PosReportParser.Parse(WithPart(4, "N1642.3E00612.5"), ReceivedAt);
        Assert.Equal(16.705, report.Latitude, 6);
        Assert.Equal(6.2083, report.Longitude, 6);
    }

    [Fact]
    public void Parse_TakesYearAndMonthFromTheReceiptTimeNotTheMessage()
    {
        var received = new DateTimeOffset(2027, 2, 20, 8, 0, 0, TimeSpan.Zero);
        var report = PosReportParser.Parse(Example, received);

        Assert.Equal(new DateTimeOffset(2027, 2, 4, 12, 5, 0, TimeSpan.Zero), report.Timestamp);
        Assert.Equal("UL20420270204RGNBKK", report.FlightId);
    }

    [Fact]
    public void Parse_DayThatDoesNotExistInTheReceiptMonth_Throws()
    {
        // Day 31 with a February receipt time. Building a DateTimeOffset from it
        // would throw an ArgumentOutOfRangeException from deep inside the BCL;
        // this turns it into a parse error that says what is actually wrong.
        var raw = WithPart(3, "311205");
        var received = new DateTimeOffset(2027, 2, 20, 8, 0, 0, TimeSpan.Zero);

        var ex = Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(raw, received));
        Assert.Contains("31", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_LowercaseInput_IsNormalisedToUppercase()
    {
        var raw = "pos/ul204.fr rgn/to bkk/041205/n1642.3e09612.5/450/12500/2800";
        var report = PosReportParser.Parse(raw, ReceivedAt);

        Assert.Equal("UL204", report.FlightNumber);
        Assert.Equal("RGN", report.Departure);
        Assert.Equal("BKK", report.Destination);
        Assert.Equal("UL20420260904RGNBKK", report.FlightId);
    }

    [Fact]
    public void Parse_SurroundingWhitespace_IsIgnored()
    {
        Assert.Equal("UL204", PosReportParser.Parse($"  {Example}\n", ReceivedAt).FlightNumber);
    }

    [Theory]
    [InlineData("POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500")]           // too few parts
    [InlineData("POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800/99")]   // too many
    [InlineData("ACK/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800")]      // wrong prefix
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_StructurallyWrongMessage_Throws(string raw)
    {
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(raw, ReceivedAt));
    }

    [Theory]
    [InlineData(1, "UL204 RGN")]        // missing .FR
    [InlineData(1, "UL204.FR RANGOON")] // departure not a 3-letter code
    [InlineData(1, ".FR RGN")]          // no flight number
    [InlineData(2, "BKK")]              // missing TO
    [InlineData(2, "TO BANGKOK")]       // destination not a 3-letter code
    [InlineData(3, "0412")]             // not 6 digits
    [InlineData(3, "042505")]           // hour 25
    [InlineData(3, "041265")]           // minute 65
    [InlineData(4, "N1642.3")]          // longitude missing
    [InlineData(4, "N1660.0E09612.5")]  // latitude minutes >= 60
    [InlineData(4, "N1642.3E09660.5")]  // longitude minutes >= 60
    [InlineData(4, "N9142.3E09612.5")]  // latitude out of range
    [InlineData(4, "X1642.3E09612.5")]  // bad hemisphere letter
    [InlineData(4, "nonsense")]
    [InlineData(5, "fast")]             // ground speed not a number
    [InlineData(6, "lots")]             // fuel on board not a number
    [InlineData(6, "-1")]               // negative fuel
    [InlineData(7, "-1")]               // negative fuel flow
    public void Parse_MalformedField_Throws(int partIndex, string value)
    {
        Assert.Throws<PosReportParseException>(
            () => PosReportParser.Parse(WithPart(partIndex, value), ReceivedAt));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-450")]
    public void Parse_NonPositiveGroundSpeed_Throws(string groundSpeed)
    {
        // The calculator divides by ground speed. Zero would give an infinite
        // remaining flight time, so it is rejected here rather than allowed to
        // become a poison message on the queue.
        var ex = Assert.Throws<PosReportParseException>(
            () => PosReportParser.Parse(WithPart(5, groundSpeed), ReceivedAt));
        Assert.Contains("Ground speed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ZeroFuel_IsAccepted()
    {
        // Zero fuel on board is alarming but it is a legitimate reading, and
        // the calculator's low-fuel warning is exactly what should surface it.
        var report = PosReportParser.Parse(WithPart(6, "0"), ReceivedAt);
        Assert.Equal(0d, report.FuelOnBoardKg);
    }

    [Theory]
    [InlineData(Example, true)]
    [InlineData("POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsRoughlyWellFormed_ChecksPrefixAndPartCountOnly(string? raw, bool expected)
    {
        Assert.Equal(expected, PosReportParser.IsRoughlyWellFormed(raw));
    }

    [Fact]
    public void IsRoughlyWellFormed_PassesMessagesThatStillFailAFullParse()
    {
        // The cheap gate is deliberately not the real parser; this documents
        // that gap rather than leaving someone to discover it.
        var raw = "POS/nope.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800";
        Assert.True(PosReportParser.IsRoughlyWellFormed(raw));
        Assert.Throws<PosReportParseException>(() => PosReportParser.Parse(raw, ReceivedAt));
    }

    [Fact]
    public void TryParse_ValidMessage_ReturnsTrueWithNoError()
    {
        Assert.True(PosReportParser.TryParse(Example, ReceivedAt, out var report, out var error));
        Assert.NotNull(report);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_InvalidMessage_ReturnsFalseWithReason()
    {
        Assert.False(PosReportParser.TryParse("rubbish", ReceivedAt, out var report, out var error));
        Assert.Null(report);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>Returns the example message with one '/'-separated part replaced.</summary>
    private static string WithPart(int index, string value)
    {
        var parts = Example.Split('/');
        parts[index] = value;
        return string.Join("/", parts);
    }
}
