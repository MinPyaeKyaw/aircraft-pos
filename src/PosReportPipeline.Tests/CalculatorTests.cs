using PosReportPipeline.Shared.Models;
using Xunit;

namespace PosReportPipeline.Tests;

public class CalculatorTests
{
    private static ParsedPosReport ReportTo(string destination) => new()
    {
        FlightId = "ABC123-20260905",
        Callsign = "ABC123",
        ReportedAtUtc = new DateTimeOffset(2026, 9, 5, 14, 20, 0, TimeSpan.Zero),
        Latitude = 37.375,
        Longitude = -122.096667,
        AltitudeFeet = 35000,
        DestinationIcao = destination,
        SourceKey = "raw/2026/09/05/abc.txt",
    };

    [Fact]
    public void Calculate_KnownDestination_PopulatesDistanceAndBearing()
    {
        var result = Calculator.Function.Calculate(ReportTo("KLAX"));

        Assert.Equal(CalculationStatus.Ok, result.CalculationStatus);
        Assert.Equal("Los Angeles International", result.DestinationName);
        Assert.NotNull(result.DistanceToDestinationKm);
        Assert.NotNull(result.DistanceToDestinationNm);
        Assert.NotNull(result.InitialBearingDegrees);

        // Bay Area to LAX is roughly 500 km on a southerly heading.
        Assert.InRange(result.DistanceToDestinationKm!.Value, 450, 600);
        Assert.InRange(result.InitialBearingDegrees!.Value, 120, 180);
    }

    [Fact]
    public void Calculate_NauticalMilesAgreeWithKilometres()
    {
        var result = Calculator.Function.Calculate(ReportTo("KLAX"));
        Assert.Equal(result.DistanceToDestinationKm!.Value / 1.852,
                     result.DistanceToDestinationNm!.Value, 2);
    }

    [Fact]
    public void Calculate_UnknownDestination_StoresPositionWithNullDistances()
    {
        var result = Calculator.Function.Calculate(ReportTo("ZZZZ"));

        Assert.Equal(CalculationStatus.UnknownDestination, result.CalculationStatus);
        Assert.Null(result.DestinationName);
        Assert.Null(result.DistanceToDestinationKm);
        Assert.Null(result.DistanceToDestinationNm);
        Assert.Null(result.InitialBearingDegrees);

        // The position itself must survive.
        Assert.Equal(37.375, result.Latitude, 6);
        Assert.Equal(-122.096667, result.Longitude, 6);
        Assert.Equal("ABC123-20260905", result.FlightId);
        Assert.Equal("ZZZZ", result.DestinationIcao);
    }

    [Fact]
    public void Calculate_CarriesThroughIdentityFields()
    {
        var result = Calculator.Function.Calculate(ReportTo("KLAX"));

        Assert.Equal("ABC123", result.Callsign);
        Assert.Equal(35000, result.AltitudeFeet);
        Assert.Equal("raw/2026/09/05/abc.txt", result.SourceKey);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 14, 20, 0, TimeSpan.Zero), result.ReportedAt);
    }
}
