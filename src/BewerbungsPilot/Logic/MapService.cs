using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Data for the "Standorte" page: confirmed map positions, straight-line distances
// to an optional starting point, route links. The starting point is only used for
// this one calculation – it is never stored or logged.
public class MapService
{
    private readonly AppDbContext _db;

    public MapService(AppDbContext db) => _db = db;

    public static bool HasCoordinates(LocationPrecision precision) =>
        precision is LocationPrecision.Exact or LocationPrecision.Approximate or LocationPrecision.City;

    public async Task<MapResponse> GetMapAsync(double? originLat, double? originLon)
    {
        var applications = await _db.Applications.AsNoTracking().ToListAsync();
        return BuildMap(applications, originLat, originLon);
    }

    public static MapResponse BuildMap(IEnumerable<JobApplication> applications, double? originLat, double? originLon)
    {
        var hasOrigin = originLat is not null && originLon is not null;
        var list = applications.ToList();

        // Several applications at the same position (rounded to ~1 m) share one marker,
        // but every application stays in the list.
        string? Key(JobApplication a) => a.MapLatitude is { } la && a.MapLongitude is { } lo && HasCoordinates(a.LocationPrecision)
            ? $"{Math.Round(la, 5)}|{Math.Round(lo, 5)}" : null;
        var groupSizes = list.Select(Key).Where(k => k is not null).GroupBy(k => k!).ToDictionary(g => g.Key, g => g.Count());

        var items = list.Select(a =>
        {
            var key = Key(a);
            var mapped = key is not null;
            double? distance = mapped && hasOrigin
                ? Math.Round(GeoMath.DistanceKm(originLat!.Value, originLon!.Value, a.MapLatitude!.Value, a.MapLongitude!.Value), 1)
                : null;
            var links = a.LocationPrecision == LocationPrecision.Remote ? null
                : NavigationLinks.Build(originLat, originLon,
                    mapped ? a.MapLatitude : null, mapped ? a.MapLongitude : null, a.MapLabel ?? a.Location);

            return new MapItem(a.Id, a.CompanyName, a.Position, a.Status, a.Location, a.LocationPrecision,
                mapped ? a.MapLatitude : null, mapped ? a.MapLongitude : null, a.MapLabel,
                distance, key is null ? 0 : groupSizes[key], links);
        })
        .OrderBy(i => i.CompanyName, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

        return new MapResponse(items, hasOrigin);
    }

    // Returns field name → errors. Coordinates are required for Exact/Approximate/City
    // and must be absent for Unknown/Remote.
    public static Dictionary<string, string[]> Validate(MapLocationInput input)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Enum.IsDefined(input.Precision))
            errors["precision"] = ["Bitte wählen Sie die Genauigkeit aus."];
        else if (HasCoordinates(input.Precision))
        {
            if (GeoMath.ValidateCoordinates(input.Latitude, input.Longitude) is { } message)
                errors["latitude"] = [message];
        }
        if (input.Label?.Trim().Length > 300)
            errors["label"] = ["Die Bezeichnung darf höchstens 300 Zeichen lang sein."];
        return errors;
    }

    // Saves a confirmed position. Returns null if the application does not exist.
    public async Task<JobApplication?> SetLocationAsync(int applicationId, MapLocationInput input)
    {
        var application = await _db.Applications.FindAsync(applicationId);
        if (application is null)
            return null;

        var withCoordinates = HasCoordinates(input.Precision);
        application.LocationPrecision = input.Precision;
        application.MapLatitude = withCoordinates ? Math.Round(input.Latitude!.Value, 6) : null;
        application.MapLongitude = withCoordinates ? Math.Round(input.Longitude!.Value, 6) : null;
        application.MapLabel = input.Precision == LocationPrecision.Unknown || string.IsNullOrWhiteSpace(input.Label)
            ? null : input.Label.Trim();

        await _db.SaveChangesAsync();
        return application;
    }
}
