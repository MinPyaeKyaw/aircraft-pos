using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

/// <summary>
/// The flight ID is the pipeline's primary key everywhere -- S3 key, both
/// DynamoDB partition keys, and the value the API hands back to callers -- so
/// it gets its own tests rather than being covered incidentally.
/// </summary>
public class FlightIdTests
{
    private static readonly DateTimeOffset Sep4 =
        new(2026, 9, 4, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void BuildFlightId_ConcatenatesFlightNumberDateDepartureDestination()
    {
        // The brief's worked example: UL204 + 20260904 + RGN + BKK.
        Assert.Equal("UL20420260904RGNBKK",
            PosReportParser.BuildFlightId("UL204", "RGN", "BKK", Sep4));
    }

    [Fact]
    public void BuildFlightId_HasNoSeparators()
    {
        var flightId = PosReportParser.BuildFlightId("UL204", "RGN", "BKK", Sep4);

        Assert.DoesNotContain("-", flightId, StringComparison.Ordinal);
        Assert.DoesNotContain("/", flightId, StringComparison.Ordinal);
        Assert.All(flightId, c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void BuildFlightId_ContainingNoDash_IsWhatMakesTheS3KeyParseable()
    {
        // PosObjectKeys.TryParseRawKey splits the raw key on its first '-'.
        // That is only unambiguous because a flight ID never contains one.
        var flightId = PosReportParser.BuildFlightId("UL204", "RGN", "BKK", Sep4);
        var receivedAt = new DateTimeOffset(2026, 9, 4, 15, 12, 30, 123, TimeSpan.Zero);

        var key = PosObjectKeys.RawKey(flightId, receivedAt);

        Assert.True(PosObjectKeys.TryParseRawKey(key, out var roundTripped, out var when));
        Assert.Equal(flightId, roundTripped);
        Assert.Equal(receivedAt, when);
    }

    [Theory]
    [InlineData("ul204", "rgn", "bkk")]
    [InlineData("  UL204  ", "  RGN  ", "  BKK  ")]
    public void BuildFlightId_NormalisesCasingAndWhitespace(
        string flightNumber, string departure, string destination)
    {
        Assert.Equal("UL20420260904RGNBKK",
            PosReportParser.BuildFlightId(flightNumber, departure, destination, Sep4));
    }

    [Fact]
    public void BuildFlightId_PadsSingleDigitMonthsAndDays()
    {
        var jan2 = new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero);
        Assert.Equal("UL20420260102RGNBKK",
            PosReportParser.BuildFlightId("UL204", "RGN", "BKK", jan2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildFlightId_BlankFlightNumber_Throws(string? flightNumber)
    {
        Assert.Throws<ArgumentException>(
            () => PosReportParser.BuildFlightId(flightNumber!, "RGN", "BKK", Sep4));
    }

    [Fact]
    public void FlightId_IsStableAcrossReceiptTimesWithinTheSameMonth()
    {
        // Two reports for the same flight, received hours apart, must land in
        // the same partition -- otherwise a flight's track would be scattered
        // across several IDs.
        const string raw = "POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800";

        var early = PosReportParser.Parse(raw, new DateTimeOffset(2026, 9, 4, 12, 6, 0, TimeSpan.Zero));
        var late = PosReportParser.Parse(raw, new DateTimeOffset(2026, 9, 4, 23, 59, 0, TimeSpan.Zero));

        Assert.Equal(early.FlightId, late.FlightId);
    }

    [Fact]
    public void FlightId_ChangesWhenTheReceiptMonthChanges()
    {
        // A known consequence of the brief's rule that the year and month come
        // from receipt time: the same message received in a different month is
        // a different flight. This is why the parser Lambda recovers the receipt
        // time from the S3 key instead of reading the clock again.
        const string raw = "POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800";

        var september = PosReportParser.Parse(raw, new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero));
        var october = PosReportParser.Parse(raw, new DateTimeOffset(2026, 10, 1, 0, 1, 0, TimeSpan.Zero));

        Assert.Equal("UL20420260904RGNBKK", september.FlightId);
        Assert.Equal("UL20420261004RGNBKK", october.FlightId);
        Assert.NotEqual(september.FlightId, october.FlightId);
    }
}
