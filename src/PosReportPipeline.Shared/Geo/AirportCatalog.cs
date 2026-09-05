namespace PosReportPipeline.Shared.Geo;

/// <summary>An airport reference point, in decimal degrees.</summary>
public sealed record Airport(string Icao, string Name, double Latitude, double Longitude);

/// <summary>
/// A hardcoded lookup of major international airports.
///
/// Pure and in-memory by design: it keeps the calculator unit-testable with no
/// I/O. Swapping it for a DynamoDB-backed catalog is a contained change behind
/// <see cref="TryGet"/>.
/// </summary>
public static class AirportCatalog
{
    private static readonly Dictionary<string, Airport> ByIcao =
        new[]
        {
            new Airport("KLAX", "Los Angeles International", 33.9425, -118.4081),
            new Airport("KJFK", "New York John F. Kennedy", 40.6413, -73.7781),
            new Airport("KSFO", "San Francisco International", 37.6213, -122.3790),
            new Airport("KORD", "Chicago O'Hare International", 41.9742, -87.9073),
            new Airport("KATL", "Atlanta Hartsfield-Jackson", 33.6407, -84.4277),
            new Airport("KDFW", "Dallas/Fort Worth International", 32.8998, -97.0403),
            new Airport("KSEA", "Seattle-Tacoma International", 47.4502, -122.3088),
            new Airport("KDEN", "Denver International", 39.8561, -104.6737),
            new Airport("KBOS", "Boston Logan International", 42.3656, -71.0096),
            new Airport("KMIA", "Miami International", 25.7959, -80.2870),
            new Airport("CYYZ", "Toronto Pearson International", 43.6777, -79.6248),
            new Airport("EGLL", "London Heathrow", 51.4700, -0.4543),
            new Airport("EGKK", "London Gatwick", 51.1537, -0.1821),
            new Airport("LFPG", "Paris Charles de Gaulle", 49.0097, 2.5479),
            new Airport("EDDF", "Frankfurt am Main", 50.0379, 8.5622),
            new Airport("EHAM", "Amsterdam Schiphol", 52.3105, 4.7683),
            new Airport("LEMD", "Madrid Barajas", 40.4839, -3.5680),
            new Airport("LIRF", "Rome Fiumicino", 41.8003, 12.2389),
            new Airport("LSZH", "Zurich", 47.4647, 8.5492),
            new Airport("LTFM", "Istanbul", 41.2753, 28.7519),
            new Airport("OMDB", "Dubai International", 25.2532, 55.3657),
            new Airport("OTHH", "Doha Hamad International", 25.2731, 51.6081),
            new Airport("VIDP", "Delhi Indira Gandhi International", 28.5562, 77.1000),
            new Airport("VABB", "Mumbai Chhatrapati Shivaji", 19.0896, 72.8656),
            new Airport("VYYY", "Yangon International", 16.9073, 96.1332),
            new Airport("VTBS", "Bangkok Suvarnabhumi", 13.6900, 100.7501),
            new Airport("WSSS", "Singapore Changi", 1.3644, 103.9915),
            new Airport("VHHH", "Hong Kong International", 22.3080, 113.9185),
            new Airport("ZBAA", "Beijing Capital International", 40.0799, 116.6031),
            new Airport("ZSPD", "Shanghai Pudong International", 31.1443, 121.8083),
            new Airport("RKSI", "Seoul Incheon International", 37.4602, 126.4407),
            new Airport("RJTT", "Tokyo Haneda", 35.5494, 139.7798),
            new Airport("RJAA", "Tokyo Narita", 35.7647, 140.3864),
            new Airport("YSSY", "Sydney Kingsford Smith", -33.9399, 151.1753),
            new Airport("NZAA", "Auckland", -37.0082, 174.7850),
            new Airport("SBGR", "Sao Paulo Guarulhos", -23.4356, -46.4731),
            new Airport("FAOR", "Johannesburg O.R. Tambo", -26.1392, 28.2460),
        }.ToDictionary(a => a.Icao, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<Airport> All => ByIcao.Values;

    /// <summary>Looks up an airport by ICAO code. Case-insensitive; trims whitespace.</summary>
    public static bool TryGet(string icao, out Airport? airport)
    {
        airport = null;
        if (string.IsNullOrWhiteSpace(icao))
        {
            return false;
        }

        return ByIcao.TryGetValue(icao.Trim(), out airport);
    }
}
