using PosReportPipeline.Shared.Geo;
using Xunit;

namespace PosReportPipeline.Tests;

public class AirportCatalogTests
{
    [Fact]
    public void TryGet_KnownCode_ReturnsAirport()
    {
        Assert.True(AirportCatalog.TryGet("KLAX", out var airport));
        Assert.NotNull(airport);
        Assert.Equal("KLAX", airport!.Icao);
        Assert.InRange(airport.Latitude, 33.9, 34.0);
        Assert.InRange(airport.Longitude, -118.5, -118.3);
    }

    [Theory]
    [InlineData("klax")]
    [InlineData("  KLAX  ")]
    [InlineData("Klax")]
    public void TryGet_IsCaseInsensitiveAndTrimsWhitespace(string code)
    {
        Assert.True(AirportCatalog.TryGet(code, out var airport));
        Assert.Equal("KLAX", airport!.Icao);
    }

    [Fact]
    public void TryGet_UnknownCode_ReturnsFalseAndNull()
    {
        Assert.False(AirportCatalog.TryGet("ZZZZ", out var airport));
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
    public void All_EntriesHaveValidCoordinatesAndFourLetterCodes()
    {
        Assert.NotEmpty(AirportCatalog.All);
        foreach (var airport in AirportCatalog.All)
        {
            Assert.Equal(4, airport.Icao.Length);
            Assert.Equal(airport.Icao.ToUpperInvariant(), airport.Icao);
            Assert.InRange(airport.Latitude, -90d, 90d);
            Assert.InRange(airport.Longitude, -180d, 180d);
            Assert.False(string.IsNullOrWhiteSpace(airport.Name));
        }
    }
}
