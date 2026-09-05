using PosReportPipeline.Shared.Geo;
using Xunit;

namespace PosReportPipeline.Tests;

public class AirportCatalogTests
{
    [Theory]
    [InlineData("RGN", 16.9073, 96.1332)]
    [InlineData("BKK", 13.6900, 100.7501)]
    [InlineData("SIN", 1.3644, 103.9915)]
    public void TryGet_ReturnsTheCoordinatesGivenInTheBrief(string code, double lat, double lon)
    {
        Assert.True(AirportCatalog.TryGet(code, out var airport));
        Assert.Equal(lat, airport!.Latitude, 4);
        Assert.Equal(lon, airport.Longitude, 4);
    }

    [Theory]
    [InlineData("rgn")]
    [InlineData("  RGN  ")]
    [InlineData("Rgn")]
    public void TryGet_IsCaseInsensitiveAndTrimsWhitespace(string code)
    {
        Assert.True(AirportCatalog.TryGet(code, out var airport));
        Assert.Equal("RGN", airport!.IataCode);
    }

    [Fact]
    public void TryGet_UnknownCode_ReturnsFalseAndNull()
    {
        Assert.False(AirportCatalog.TryGet("ZZZ", out var airport));
        Assert.Null(airport);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGet_NullOrBlank_ReturnsFalse(string? code)
    {
        Assert.False(AirportCatalog.TryGet(code!, out var airport));
        Assert.Null(airport);
    }

    [Fact]
    public void All_EntriesAreThreeLetterCodesWithValidCoordinates()
    {
        Assert.NotEmpty(AirportCatalog.All);
        foreach (var airport in AirportCatalog.All)
        {
            Assert.Equal(3, airport.IataCode.Length);
            Assert.Equal(airport.IataCode.ToUpperInvariant(), airport.IataCode);
            Assert.InRange(airport.Latitude, -90d, 90d);
            Assert.InRange(airport.Longitude, -180d, 180d);
            Assert.False(string.IsNullOrWhiteSpace(airport.Name));
        }
    }
}
