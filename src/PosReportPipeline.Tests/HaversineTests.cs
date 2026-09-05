using PosReportPipeline.Shared.Geo;
using Xunit;

namespace PosReportPipeline.Tests;

/// <summary>
/// Expectations are derived from the sphere the formula assumes -- a quarter
/// great circle, a half circle, one degree of arc -- rather than copied from
/// published city-pair distances, which vary between sources and would make
/// these tests fail for reasons that have nothing to do with the code.
/// </summary>
public class HaversineTests
{
    private const double QuarterCircleNm = 5403.641466;  // 2*PI*R/4
    private const double HalfCircleNm = 10807.282932;    // 2*PI*R/2
    private const double OneDegreeNm = 60.040461;        // 2*PI*R/360

    [Fact]
    public void DistanceNm_IdenticalPoints_IsZero()
    {
        Assert.Equal(0d, Haversine.DistanceNm(16.705, 96.2083, 16.705, 96.2083), 9);
    }

    [Fact]
    public void DistanceNm_OneDegreeOfLatitude_IsAboutSixtyNauticalMiles()
    {
        // A nautical mile was defined as one minute of latitude, so one degree
        // must come out at very close to 60. This is the check that the earth
        // radius constant is in the right unit at all.
        var actual = Haversine.DistanceNm(0, 0, 1, 0);
        Assert.Equal(OneDegreeNm, actual, 4);
        Assert.InRange(actual, 59.9, 60.1);
    }

    [Fact]
    public void DistanceNm_QuarterWayAroundEquator_IsQuarterCircumference()
    {
        Assert.Equal(QuarterCircleNm, Haversine.DistanceNm(0, 0, 0, 90), 4);
    }

    [Fact]
    public void DistanceNm_EquatorToNorthPole_IsQuarterCircumference()
    {
        Assert.Equal(QuarterCircleNm, Haversine.DistanceNm(0, 0, 90, 0), 4);
    }

    [Fact]
    public void DistanceNm_AntipodalOnEquator_IsHalfCircumference()
    {
        Assert.Equal(HalfCircleNm, Haversine.DistanceNm(0, 0, 0, 180), 4);
    }

    [Fact]
    public void DistanceNm_IsSymmetric()
    {
        var forward = Haversine.DistanceNm(16.705, 96.2083, 13.69, 100.7501);
        var reverse = Haversine.DistanceNm(13.69, 100.7501, 16.705, 96.2083);
        Assert.Equal(forward, reverse, 9);
    }

    [Fact]
    public void DistanceNm_WorkedExampleFromTheBrief_IsAboutThreeHundredNineteen()
    {
        // The brief's worked example: current position to BKK is "approximately
        // 319 nautical miles".
        var actual = Haversine.DistanceNm(16.705, 96.2083, 13.6900, 100.7501);
        Assert.Equal(319.37, actual, 1);
    }

    [Fact]
    public void DistanceNm_HandlesSouthernAndWesternHemispheres()
    {
        // Sydney to Dubai, both signs exercised, sanity-checked against the
        // well-known ~6,400 nm figure for that sector.
        var actual = Haversine.DistanceNm(-33.9399, 151.1753, 25.2532, 55.3657);
        Assert.InRange(actual, 6200, 6600);
    }
}
