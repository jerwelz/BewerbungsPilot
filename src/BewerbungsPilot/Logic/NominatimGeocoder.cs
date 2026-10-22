using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using BewerbungsPilot.Data;

namespace BewerbungsPilot.Logic;

// Looks up places with Nominatim (OpenStreetMap Foundation) – ONLY after the user clicks
// "Suchen". Usage policy: https://operations.osmfoundation.org/policies/nominatim/
// - at most 1 request per second (enforced below for this whole application)
// - identifying User-Agent (configured in appsettings.json, "Geocoding")
// - results are cached (per user); no automatic or bulk requests, no search-as-you-type
// - attribution "© OpenStreetMap-Mitwirkende" is shown with the results
// Search texts are not logged (see the HttpClient log level in appsettings.json).
public class NominatimGeocoder
{
    public const string Attribution = "Daten © OpenStreetMap-Mitwirkende, ODbL · Suche: Nominatim";
    private const int MaxCacheEntries = 200;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);
    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static readonly ConcurrentDictionary<string, List<GeocodeResult>> Cache = new();

    private readonly HttpClient _http;
    private readonly CurrentUser _user;

    public NominatimGeocoder(HttpClient http, CurrentUser user)
    {
        _http = http;
        _user = user;
    }

    public static string? ValidateQuery(string? query)
    {
        var length = query?.Trim().Length ?? 0;
        if (length < 3) return "Bitte geben Sie mindestens 3 Zeichen ein.";
        if (length > 200) return "Die Suche darf höchstens 200 Zeichen lang sein.";
        return null;
    }

    public async Task<List<GeocodeResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        // Cache per user: nobody can find out (e.g. by timing) what another user searched for.
        var key = $"{_user.UserId}|{query.Trim().ToLowerInvariant()}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        await OneAtATime.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastRequestUtc.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cancellationToken);

            var url = $"search?q={Uri.EscapeDataString(query.Trim())}&format=jsonv2&limit=5&accept-language=de";
            using var response = await _http.GetAsync(url, cancellationToken);
            _lastRequestUtc = DateTime.UtcNow;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var results = Parse(await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken));

            if (Cache.Count >= MaxCacheEntries)
                Cache.Clear();
            Cache[key] = results;
            return results;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    // Nominatim returns lat/lon as strings. Invalid entries are skipped, never "repaired".
    public static List<GeocodeResult> Parse(JsonDocument document)
    {
        var results = new List<GeocodeResult>();
        foreach (var place in document.RootElement.EnumerateArray())
        {
            if (!double.TryParse(place.GetProperty("lat").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !double.TryParse(place.GetProperty("lon").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || GeoMath.ValidateCoordinates(lat, lon) is not null)
                continue;

            var kind = place.TryGetProperty("addresstype", out var type) ? type.GetString() ?? "" : "";
            results.Add(new GeocodeResult(place.GetProperty("display_name").GetString() ?? "", lat, lon,
                SuggestPrecision(kind), kind));
        }
        return results;
    }

    // Only a SUGGESTION for the dialog – the user decides. A house address is not
    // automatically the workplace (it might be the head office).
    public static LocationPrecision SuggestPrecision(string addressType) => addressType switch
    {
        "house" or "building" or "amenity" or "office" or "shop" => LocationPrecision.Exact,
        "city" or "town" or "village" or "municipality" or "county" or "state" or "hamlet" => LocationPrecision.City,
        _ => LocationPrecision.Approximate
    };
}
