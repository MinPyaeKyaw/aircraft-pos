namespace PosReportPipeline.Shared.Geo;

/// <summary>
/// Great-circle distance and bearing on a spherical earth.
/// </summary>
public static class Haversine
{
    /// <summary>IUGG mean earth radius, in kilometres.</summary>
    public const double EarthRadiusKm = 6371.0088;

    /// <summary>The international nautical mile, in kilometres. Exact by definition.</summary>
    public const double KmPerNauticalMile = 1.852;

    /// <summary>Great-circle distance between two points, in kilometres.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var deltaPhi = ToRadians(lat2 - lat1);
        var deltaLambda = ToRadians(lon2 - lon1);

        var a = Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2)
              + Math.Cos(phi1) * Math.Cos(phi2)
              * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2);

        // Clamp guards against a > 1 from floating-point error near antipodes,
        // which would make Sqrt return NaN.
        var c = 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0d, 1d)));

        return EarthRadiusKm * c;
    }

    /// <summary>
    /// Initial great-circle bearing from the first point to the second, in
    /// degrees clockwise from true north, normalised to [0, 360).
    /// </summary>
    public static double InitialBearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var deltaLambda = ToRadians(lon2 - lon1);

        var y = Math.Sin(deltaLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2)
              - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLambda);

        var bearing = ToDegrees(Math.Atan2(y, x));

        // Atan2 returns (-180, 180]; shift into [0, 360).
        return (bearing + 360) % 360;
    }

    public static double ToNauticalMiles(double kilometres) => kilometres / KmPerNauticalMile;

    private static double ToRadians(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
