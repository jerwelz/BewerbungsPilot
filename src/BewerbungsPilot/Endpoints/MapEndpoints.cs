using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

// HTTP layer for the "Standorte" page. Rules live in MapService / GeoMath / NominatimGeocoder.
// POST is used for the map query and the place search so that the starting point and
// search texts travel in the request body, not in URLs (which may end up in logs).
public static class MapEndpoints
{
    public static void MapMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/map/query", Query);
        app.MapPut("/api/applications/{id:int}/map-location", SetLocation);
        app.MapPost("/api/geocode", Geocode);
    }

    // POST /api/map/query   body: { "origin": { "latitude": 53.55, "longitude": 9.99 } } or { "origin": null }
    private static async Task<IResult> Query(MapQuery? query, MapService service)
    {
        var origin = query?.Origin;
        if (origin is not null && GeoMath.ValidateCoordinates(origin.Latitude, origin.Longitude) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["origin"] = [error] },
                title: "Ungültiger Startpunkt.");
        return Results.Ok(await service.GetMapAsync(origin?.Latitude, origin?.Longitude));
    }

    // PUT /api/applications/5/map-location
    private static async Task<IResult> SetLocation(int id, MapLocationInput input, MapService service)
    {
        var errors = MapService.Validate(input);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie den Standort.");

        var application = await service.SetLocationAsync(id, input);
        return application is null
            ? Results.Problem(title: "Diese Bewerbung wurde nicht gefunden. Möglicherweise wurde sie bereits gelöscht.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(MapService.BuildMap([application], null, null).Items[0]);
    }

    // POST /api/geocode   body: { "query": "Hafenstraße 12, Hamburg" } – sent to Nominatim
    private static async Task<IResult> Geocode(GeocodeQuery input, NominatimGeocoder geocoder, IConfiguration configuration, CancellationToken cancellationToken)
    {
        // Public demo: no requests to Nominatim on behalf of anonymous visitors (usage policy).
        if (DemoAccounts.IsEnabled(configuration))
            return Results.Problem(title: "In der Demo ist die Ortssuche deaktiviert. Sie können die Koordinaten von Hand eingeben.",
                statusCode: StatusCodes.Status403Forbidden);

        if (NominatimGeocoder.ValidateQuery(input.Query) is { } error)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["query"] = [error] },
                title: "Bitte prüfen Sie die Suche.");
        try
        {
            var results = await geocoder.SearchAsync(input.Query!, cancellationToken);
            return Results.Ok(new GeocodeResponse(results, NominatimGeocoder.Attribution));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return Results.Problem(
                title: "Der Suchdienst (Nominatim) ist gerade nicht erreichbar. Sie können die Koordinaten auch von Hand eingeben.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
