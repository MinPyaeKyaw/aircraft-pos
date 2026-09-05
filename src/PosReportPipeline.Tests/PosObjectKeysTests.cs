using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

/// <summary>
/// The key formats are a contract between the four Lambdas: the API writes a
/// key the parser has to read back, and both DynamoDB sort keys are the same
/// strings. Getting one of them subtly wrong would not fail loudly, so they are
/// pinned here.
/// </summary>
public class PosObjectKeysTests
{
    private const string FlightId = "UL20420260904RGNBKK";

    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 4, 15, 12, 30, 123, TimeSpan.Zero);

    private static readonly DateTimeOffset ReportTime =
        new(2026, 9, 4, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public void RawKey_MatchesTheFormatInTheBrief()
    {
        Assert.Equal(
            "pos/UL20420260904RGNBKK-2026-09-04T15:12:30.123Z",
            PosObjectKeys.RawKey(FlightId, ReceivedAt));
    }

    [Fact]
    public void AttachmentKey_MatchesTheFormatInTheBrief()
    {
        Assert.Equal(
            "attachment/UL20420260904RGNBKK-2026-09-04T12:05:00Z.json",
            PosObjectKeys.AttachmentKey(FlightId, ReportTime));
    }

    [Fact]
    public void ResultKey_MatchesTheFormatInTheBrief()
    {
        var calculatedAt = new DateTimeOffset(2026, 9, 4, 12, 6, 15, 842, TimeSpan.Zero);

        Assert.Equal(
            "results/UL20420260904RGNBKK-2026-09-04T12:06:15.842Z.json",
            PosObjectKeys.ResultKey(FlightId, calculatedAt));
    }

    [Fact]
    public void TryParseRawKey_RoundTripsFlightIdAndReceiptTime()
    {
        var key = PosObjectKeys.RawKey(FlightId, ReceivedAt);

        Assert.True(PosObjectKeys.TryParseRawKey(key, out var flightId, out var receivedAt));
        Assert.Equal(FlightId, flightId);
        Assert.Equal(ReceivedAt, receivedAt);
    }

    [Theory]
    [InlineData("attachment/UL20420260904RGNBKK-2026-09-04T12:05:00Z.json")] // wrong prefix
    [InlineData("pos/UL20420260904RGNBKK")]                                  // no timestamp
    [InlineData("pos/-2026-09-04T15:12:30.123Z")]                            // no flight id
    [InlineData("pos/UL20420260904RGNBKK-not-a-timestamp")]
    [InlineData("pos/UL20420260904RGNBKK-2026-09-04T15:12:30Z")]             // missing milliseconds
    [InlineData("")]
    public void TryParseRawKey_RejectsAnythingElse(string key)
    {
        Assert.False(PosObjectKeys.TryParseRawKey(key, out _, out _));
    }

    [Fact]
    public void Timestamps_SortLexicographicallyInChronologicalOrder()
    {
        // Both tables rely on this: "latest report" is a descending query on the
        // sort key, which is only correct if string order matches time order.
        var earlier = PosObjectKeys.FormatMilliseconds(new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero));
        var later = PosObjectKeys.FormatMilliseconds(new DateTimeOffset(2026, 9, 4, 10, 0, 0, TimeSpan.Zero));
        var nextYear = PosObjectKeys.FormatMilliseconds(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
        Assert.True(string.CompareOrdinal(later, nextYear) < 0);
    }

    [Fact]
    public void FormatMilliseconds_AlwaysEndsWithZAndNeverAnOffset()
    {
        var local = new DateTimeOffset(2026, 9, 4, 22, 12, 30, 123, TimeSpan.FromHours(7));

        var formatted = PosObjectKeys.FormatMilliseconds(local);

        Assert.EndsWith("Z", formatted, StringComparison.Ordinal);
        Assert.Equal("2026-09-04T15:12:30.123Z", formatted);
    }
}
