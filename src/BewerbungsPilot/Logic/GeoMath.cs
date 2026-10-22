using System.Globalization;

namespace BewerbungsPilot.Logic;

// Geographic helpers. All distances are straight-line distances ("Luftlinie") –
// never driving or commuting distances.
public static class GeoMath
{
    private const double EarthRadiusKm = 6371.0088; // mean Earth radius

    // Haversine formula: great-circle distance between two points.
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        static double Radians(double degrees) => degrees * Math.PI / 180;

        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
                + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // Returns a German error message, or null if the coordinates are valid.
    public static string? ValidateCoordinates(double? latitude, double? longitude)
    {
        if (latitude is not { } lat || longitude is not { } lon)
            return "Bitte geben Sie Breiten- und Längengrad an.";
        if (!double.IsFinite(lat) || lat < -90 || lat > 90)
            return "Der Breitengrad muss zwischen −90 und 90 liegen.";
        if (!double.IsFinite(lon) || lon < -180 || lon > 180)
            return "Der Längengrad muss zwischen −180 und 180 liegen.";
        if (lat == 0 && lon == 0)
            return "Die Koordinaten 0/0 liegen im Atlantik – vermutlich fehlen die Werte.";
        return null;
    }

    // Invariant formatting ("53.5511") – used in URLs, independent of the server's language.
    public static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
