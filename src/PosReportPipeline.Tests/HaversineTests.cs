using PosReportPipeline.Shared.Geo;
using Xunit;

namespace PosReportPipeline.Tests;

/// <summary>
/// Expectations here are analytically exact for a sphere -- a quarter great
/// circle, a half circle, one degree of arc -- rather than published city-pair
/// distances, which differ by tens of kilometres between sources and would
/// make these tests fail for reasons unrelated to the code.
/// </summary>
public class HaversineTests
{
    private const double QuarterCircleKm = 10007.557221; // 2*PI*R/4
    private const double HalfCircleKm = 20015.114442;    // 2*PI*R/2
    private const double OneDegreeKm = 111.195080;       // 2*PI*R/360

    [Fact]
    public void DistanceKm_IdenticalPoints_IsZero()
    {
        Assert.Equal(0d, Haversine.DistanceKm(37.375, -122.0967, 37.375, -122.0967), 6);
    }

    [Fact]
    public void DistanceKm_QuarterWayAroundEquator_IsQuarterCircumference()
    {
        Assert.Equal(QuarterCircleKm, Haversine.DistanceKm(0, 0, 0, 90), 3);
    }

    [Fact]
    public void DistanceKm_EquatorToNorthPole_IsQuarterCircumference()
    {
        Assert.Equal(QuarterCircleKm, Haversine.DistanceKm(0, 0, 90, 0), 3);
    }

    [Fact]
    public void DistanceKm_AntipodalOnEquator_IsHalfCircumference()
    {
        Assert.Equal(HalfCircleKm, Haversine.DistanceKm(0, 0, 0, 180), 3);
    }

    [Fact]
    public void DistanceKm_OneDegreeOfLatitude_IsOneDegreeOfArc()
    {
        Assert.Equal(OneDegreeKm, Haversine.DistanceKm(0, 0, 1, 0), 3);
    }

    [Fact]
    public void DistanceKm_IsSymmetric()
    {
        var forward = Haversine.DistanceKm(33.9425, -118.4081, 40.6413, -73.7781);
        var reverse = Haversine.DistanceKm(40.6413, -73.7781, 33.9425, -118.4081);
        Assert.Equal(forward, reverse, 9);
    }

    [Fact]
    public void DistanceKm_LosAngelesToNewYork_IsAboutThreeThousandNineHundredSeventyKm()
    {
        // Sanity check on units only, hence the loose tolerance.
        var actual = Haversine.DistanceKm(33.9425, -118.4081, 40.6413, -73.7781);
        Assert.InRange(actual, 3970 * 0.99, 3970 * 1.01);
    }

    [Theory]
    [InlineData(1, 0, 0)]      // due north
    [InlineData(0, 1, 90)]     // due east
    [InlineData(-1, 0, 180)]   // due south
    [InlineData(0, -1, 270)]   // due west
    public void InitialBearingDegrees_CardinalDirectionsFromOrigin(
        double toLat, double toLon, double expected)
    {
        Assert.Equal(expected, Haversine.InitialBearingDegrees(0, 0, toLat, toLon), 6);
    }

    [Fact]
    public void InitialBearingDegrees_IsNormalisedToZeroToThreeSixty()
    {
        // Heading west-north-west must come back as ~338, never as -22.
        var bearing = Haversine.InitialBearingDegrees(0, 0, 1, -0.4);
        Assert.InRange(bearing, 0, 360);
        Assert.InRange(bearing, 330, 345);
    }

    [Fact]
    public void ToNauticalMiles_UsesExactInternationalNauticalMile()
    {
        Assert.Equal(1d, Haversine.ToNauticalMiles(1.852), 9);
    }
}
