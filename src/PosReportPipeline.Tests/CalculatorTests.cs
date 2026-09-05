using System.Text.Json;
using PosReportPipeline.Calculator;
using PosReportPipeline.Shared.Models;
using PosReportPipeline.Shared.Parsing;
using Xunit;

namespace PosReportPipeline.Tests;

public class CalculatorTests
{
    private const string Example = "POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/450/12500/2800";

    private static readonly DateTimeOffset ReceivedAt =
        new(2026, 9, 4, 15, 12, 30, 123, TimeSpan.Zero);

    /// <summary>The calculator's own clock reading from the brief's example.</summary>
    private static readonly DateTimeOffset CalculatedAt =
        new(2026, 9, 4, 12, 6, 15, 842, TimeSpan.Zero);

    private static ParsedPosReport Report(
        string destination = "BKK",
        int groundSpeedKnots = 450,
        double fuelOnBoardKg = 12500,
        double fuelFlowKgPerHour = 2800)
    {
        var parsed = PosReportParser.Parse(Example, ReceivedAt);
        return parsed with
        {
            Destination = destination,
            GroundSpeedKnots = groundSpeedKnots,
            FuelOnBoardKg = fuelOnBoardKg,
            FuelFlowKgPerHour = fuelFlowKgPerHour,
        };
    }

    [Fact]
    public void Calculate_TheBriefsWorkedExample_ReproducesItsNumbers()
    {
        // The brief: ~319 nm, ~43 minutes, ~10513 kg at arrival.
        var result = Function.Calculate(Report(), CalculatedAt);

        Assert.Equal(43, result.RemainingFlightTimeMinutes);
        Assert.Equal(10513d, result.EstimatedFuelAtArrivalKg);
        Assert.False(result.LowFuelWarning);
    }

    [Fact]
    public void Calculate_CarriesItsInputsIntoTheResult()
    {
        // The stored result has to be self-contained: readable months later
        // without re-fetching the report it came from.
        var result = Function.Calculate(Report(), CalculatedAt);

        Assert.Equal("UL20420260904RGNBKK", result.FlightId);
        Assert.Equal(CalculatedAt, result.Timestamp);
        Assert.Equal(16.705, result.Input.CurrentLatitude, 4);
        Assert.Equal(96.2083, result.Input.CurrentLongitude, 4);
        Assert.Equal("BKK", result.Input.Destination);
        Assert.Equal(450, result.Input.GroundSpeedKnots);
        Assert.Equal(12500d, result.Input.FuelOnBoardKg);
        Assert.Equal(2800d, result.Input.FuelFlowKgPerHour);
    }

    [Fact]
    public void Calculate_ResultTimestampIsTheCalculationTimeNotTheReportTime()
    {
        var report = Report();
        var result = Function.Calculate(report, CalculatedAt);

        Assert.Equal(CalculatedAt, result.Timestamp);
        Assert.NotEqual(report.Timestamp, result.Timestamp);
    }

    [Fact]
    public void Calculate_NegativeArrivalFuel_RaisesTheLowFuelWarning()
    {
        // 500 kg on board burning 2800 kg/h cannot cover a 43-minute leg.
        var result = Function.Calculate(Report(fuelOnBoardKg: 500), CalculatedAt);

        Assert.True(result.LowFuelWarning);
        Assert.Equal(-1487d, result.EstimatedFuelAtArrivalKg);
    }

    // The leg burns 1987.19 kg: 2800 kg/h for 0.70971 h over 319.37 nm. The two
    // cases below sit either side of that, one kilogram apart.

    [Fact]
    public void Calculate_FuelJustAboveWhatTheLegNeeds_DoesNotWarn()
    {
        var result = Function.Calculate(Report(fuelOnBoardKg: 1988), CalculatedAt);

        Assert.False(result.LowFuelWarning);
        Assert.Equal(1d, result.EstimatedFuelAtArrivalKg);
    }

    [Fact]
    public void Calculate_FuelJustBelowWhatTheLegNeeds_WarnsEvenThoughItRoundsToZero()
    {
        // 1987 kg leaves -0.19 kg, which rounds to 0 for storage. The warning is
        // computed from the real figure, not the rounded one -- otherwise an
        // aircraft landing just short would be reported as landing exactly on
        // empty, with no warning at all.
        var result = Function.Calculate(Report(fuelOnBoardKg: 1987), CalculatedAt);

        Assert.True(result.LowFuelWarning);
        Assert.Equal(0d, result.EstimatedFuelAtArrivalKg, 3);
    }

    [Fact]
    public void Calculate_HigherGroundSpeed_ShortensTheFlightAndSavesFuel()
    {
        var slow = Function.Calculate(Report(groundSpeedKnots: 300), CalculatedAt);
        var fast = Function.Calculate(Report(groundSpeedKnots: 600), CalculatedAt);

        Assert.True(fast.RemainingFlightTimeMinutes < slow.RemainingFlightTimeMinutes);
        Assert.True(fast.EstimatedFuelAtArrivalKg > slow.EstimatedFuelAtArrivalKg);
    }

    [Fact]
    public void Calculate_UnknownDestination_ThrowsAPermanentFailure()
    {
        // Permanent on purpose: no amount of retrying adds an airport to a
        // hardcoded table, so this must not be allowed to consume DLQ retries.
        var ex = Assert.Throws<PermanentCalculationException>(
            () => Function.Calculate(Report(destination: "ZZZ"), CalculatedAt));

        Assert.Contains("ZZZ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Calculate_AlreadyOverhead_IsZeroTimeAndNoFuelBurn()
    {
        var report = PosReportParser.Parse(Example, ReceivedAt) with
        {
            Latitude = 13.6900,
            Longitude = 100.7501,
            Destination = "BKK",
        };

        var result = Function.Calculate(report, CalculatedAt);

        Assert.Equal(0, result.RemainingFlightTimeMinutes);
        Assert.Equal(12500d, result.EstimatedFuelAtArrivalKg);
        Assert.False(result.LowFuelWarning);
    }

    [Fact]
    public void Calculate_SerialisesToTheResultJsonShapeInTheBrief()
    {
        var json = JsonSerializer.Serialize(
            Function.Calculate(Report(), CalculatedAt), PosJson.Options);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("UL20420260904RGNBKK", root.GetProperty("flightId").GetString());
        Assert.Equal("2026-09-04T12:06:15.842Z", root.GetProperty("timestamp").GetString());
        Assert.Equal(43, root.GetProperty("remainingFlightTimeMinutes").GetInt32());
        Assert.Equal(10513d, root.GetProperty("estimatedFuelAtArrivalKg").GetDouble());
        Assert.False(root.GetProperty("lowFuelWarning").GetBoolean());

        var input = root.GetProperty("input");
        Assert.Equal("BKK", input.GetProperty("destination").GetString());
        Assert.Equal(450, input.GetProperty("groundSpeedKnots").GetInt32());
        Assert.Equal(16.705, input.GetProperty("currentLatitude").GetDouble(), 4);
        Assert.Equal(96.2083, input.GetProperty("currentLongitude").GetDouble(), 4);
        Assert.Equal(12500d, input.GetProperty("fuelOnBoardKg").GetDouble());
        Assert.Equal(2800d, input.GetProperty("fuelFlowKgPerHour").GetDouble());
    }
}
