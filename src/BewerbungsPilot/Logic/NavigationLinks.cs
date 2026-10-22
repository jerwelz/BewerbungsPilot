namespace BewerbungsPilot.Logic;

// Links that open a route in Google Maps (documented "Maps URLs", api=1):
// https://developers.google.com/maps/documentation/urls/get-started#directions-action
// BewerbungsPilot calculates no route and no travel time – Google does that after the click.
public static class NavigationLinks
{
    private const string Base = "https://www.google.com/maps/dir/?api=1";

    // Only these documented travel modes are ever put into a URL.
    public static readonly IReadOnlyDictionary<string, string> TravelModes = new Dictionary<string, string>
    {
        ["transit"] = "ÖPNV",
        ["driving"] = "Auto",
        ["bicycling"] = "Fahrrad",
        ["walking"] = "Zu Fuß"
    };

    // Destination: confirmed coordinates, otherwise the free-text location (e.g. "Hamburg").
    // Without origin, Google uses the device's current position (if the user allows it).
    // Returns null if there is no destination at all.
    public static RouteLinks? Build(double? originLat, double? originLon, double? destLat, double? destLon, string? destText)
    {
        string? destination = destLat is { } la && destLon is { } lo
            ? $"{GeoMath.Format(la)},{GeoMath.Format(lo)}"
            : string.IsNullOrWhiteSpace(destText) ? null : destText.Trim();
        if (destination is null)
            return null;

        var origin = originLat is { } ola && originLon is { } olo
            ? $"&origin={Uri.EscapeDataString($"{GeoMath.Format(ola)},{GeoMath.Format(olo)}")}"
            : "";

        // Every value is percent-encoded – a location text like "A&B?x=1" cannot add parameters.
        string Url(string mode) => $"{Base}{origin}&destination={Uri.EscapeDataString(destination)}&travelmode={mode}";
        return new RouteLinks(Url("transit"), Url("driving"), Url("bicycling"), Url("walking"));
    }
}
