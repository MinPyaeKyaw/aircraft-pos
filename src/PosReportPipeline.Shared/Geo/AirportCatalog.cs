namespace PosReportPipeline.Shared.Geo;

/// <summary>An airport reference point, in decimal degrees.</summary>
public sealed record Airport(string IataCode, string Name, double Latitude, double Longitude);

/// <summary>
/// A hardcoded IATA code to lat/lon lookup.
///
/// Hardcoded on purpose, per the assessment. It is pure and needs no I/O,
/// which is what lets the calculation be unit-tested without any AWS mocks.
/// Replacing it with a table-backed catalog is a contained change behind
/// <see cref="TryGet"/>.
/// </summary>
public static class AirportCatalog
{
    private static readonly Dictionary<string, Airport> ByIata =
        new[]
        {
            // The three the assessment names.
            new Airport("RGN", "Yangon", 16.9073, 96.1332),
            new Airport("BKK", "Bangkok Suvarnabhumi", 13.6900, 100.7501),
            new Airport("SIN", "Singapore Changi", 1.3644, 103.9915),

            // Enough regional and long-haul coverage for the test messages.
            new Airport("MDL", "Mandalay", 21.7022, 95.9779),
            new Airport("NYT", "Nay Pyi Taw", 19.6234, 96.2010),
            new Airport("KUL", "Kuala Lumpur", 2.7456, 101.7099),
            new Airport("HAN", "Hanoi Noi Bai", 21.2212, 105.8072),
            new Airport("SGN", "Ho Chi Minh City", 10.8188, 106.6520),
            new Airport("CGK", "Jakarta Soekarno-Hatta", -6.1256, 106.6559),
            new Airport("MNL", "Manila", 14.5086, 121.0198),
            new Airport("HKG", "Hong Kong", 22.3080, 113.9185),
            new Airport("PVG", "Shanghai Pudong", 31.1443, 121.8083),
            new Airport("ICN", "Seoul Incheon", 37.4602, 126.4407),
            new Airport("NRT", "Tokyo Narita", 35.7647, 140.3864),
            new Airport("DEL", "Delhi", 28.5562, 77.1000),
            new Airport("BOM", "Mumbai", 19.0896, 72.8656),
            new Airport("CMB", "Colombo", 7.1808, 79.8841),
            new Airport("DXB", "Dubai", 25.2532, 55.3657),
            new Airport("LHR", "London Heathrow", 51.4700, -0.4543),
            new Airport("SYD", "Sydney", -33.9399, 151.1753),
        }.ToDictionary(a => a.IataCode, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<Airport> All => ByIata.Values;

    /// <summary>Looks up an airport by IATA code. Case-insensitive; trims whitespace.</summary>
    public static bool TryGet(string iataCode, out Airport? airport)
    {
        airport = null;
        if (string.IsNullOrWhiteSpace(iataCode))
        {
            return false;
        }

        if (!ByIata.TryGetValue(iataCode.Trim(), out var found))
        {
            return false;
        }

        airport = found;
        return true;
    }
}
