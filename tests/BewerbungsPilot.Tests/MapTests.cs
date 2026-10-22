using System.Globalization;
using System.Text.Json;
using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BewerbungsPilot.Tests;

public class MapTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    private const double HamburgLat = 53.5511, HamburgLon = 9.9937;
    private const double BerlinLat = 52.5200, BerlinLon = 13.4050;

    public void Dispose() => _database.Dispose();

    private static JobApplication App(int id, string company, LocationPrecision precision = LocationPrecision.Unknown,
        double? lat = null, double? lon = null, string? location = null) => new()
    {
        Id = id, CompanyName = company, Position = "Azubi FIAE", Location = location, Notes = "privat",
        LocationPrecision = precision, MapLatitude = lat, MapLongitude = lon
    };

    // ---------- Distances ----------

    [Fact]
    public void Haversine_HamburgToBerlin_IsAbout255Km()
    {
        Assert.InRange(GeoMath.DistanceKm(HamburgLat, HamburgLon, BerlinLat, BerlinLon), 254, 257);
    }

    [Fact]
    public void Haversine_SamePoint_IsZero_AndIsSymmetric()
    {
        Assert.Equal(0, GeoMath.DistanceKm(HamburgLat, HamburgLon, HamburgLat, HamburgLon), precision: 9);
        Assert.Equal(GeoMath.DistanceKm(HamburgLat, HamburgLon, BerlinLat, BerlinLon),
            GeoMath.DistanceKm(BerlinLat, BerlinLon, HamburgLat, HamburgLon), precision: 9);
    }

    // ---------- Coordinate validation ----------

    [Theory]
    [InlineData(null, 10.0)]
    [InlineData(91.0, 10.0)]
    [InlineData(-90.5, 10.0)]
    [InlineData(53.0, 180.5)]
    [InlineData(double.NaN, 10.0)]
    [InlineData(53.0, double.PositiveInfinity)]
    [InlineData(0.0, 0.0)]
    public void InvalidCoordinates_AreRejected(double? lat, double? lon)
    {
        Assert.NotNull(GeoMath.ValidateCoordinates(lat, lon));
    }

    [Theory]
    [InlineData(53.5511, 9.9937)]
    [InlineData(-33.8688, 151.2093)]
    [InlineData(90.0, -180.0)]
    public void ValidCoordinates_AreAccepted(double lat, double lon)
    {
        Assert.Null(GeoMath.ValidateCoordinates(lat, lon));
    }

    [Theory]
    [InlineData(LocationPrecision.Exact)]
    [InlineData(LocationPrecision.Approximate)]
    [InlineData(LocationPrecision.City)]
    public void PrecisionWithPosition_RequiresCoordinates(LocationPrecision precision)
    {
        Assert.True(MapService.Validate(new MapLocationInput(precision, null, null, "Hamburg")).ContainsKey("latitude"));
    }

    [Theory]
    [InlineData(LocationPrecision.Remote)]
    [InlineData(LocationPrecision.Unknown)]
    public void RemoteAndUnknown_NeedNoCoordinates(LocationPrecision precision)
    {
        Assert.Empty(MapService.Validate(new MapLocationInput(precision, null, null, null)));
    }

    // ---------- Saving positions ----------

    private async Task<int> CreateAsync(string company)
    {
        using var db = _database.CreateContext();
        return (await new ApplicationService(db, _clock, _database.Storage).CreateAsync(
            new ApplicationInput(company, "Azubi", "Hamburg", null, null, null, ApplicationStatus.Entwurf, "Notiz"))).Id;
    }

    private async Task<JobApplication?> SetAsync(int id, MapLocationInput input)
    {
        using var db = _database.CreateContext();
        return await new MapService(db).SetLocationAsync(id, input);
    }

    [Fact]
    public async Task SetLocation_StoresConfirmedPosition_AndKeepsOtherFields()
    {
        var id = await CreateAsync("Nordlicht");

        await SetAsync(id, new MapLocationInput(LocationPrecision.Exact, 53.5446789012, 9.9661234567, "  Hafenstraße 12, Hamburg "));

        using var db = _database.CreateContext();
        var saved = await db.Applications.SingleAsync();
        Assert.Equal((LocationPrecision.Exact, 53.544679, 9.966123, "Hafenstraße 12, Hamburg"),
            (saved.LocationPrecision, saved.MapLatitude!.Value, saved.MapLongitude!.Value, saved.MapLabel));
        Assert.Equal(("Hamburg", "Notiz"), (saved.Location, saved.Notes)); // typed location and notes untouched
    }

    [Fact]
    public async Task ChangeAndRemoveLocation()
    {
        var id = await CreateAsync("Nordlicht");
        await SetAsync(id, new MapLocationInput(LocationPrecision.City, HamburgLat, HamburgLon, "Hamburg"));

        await SetAsync(id, new MapLocationInput(LocationPrecision.Remote, HamburgLat, HamburgLon, "Remote (Deutschland)"));
        using (var db = _database.CreateContext())
        {
            var remote = await db.Applications.SingleAsync();
            Assert.Equal(LocationPrecision.Remote, remote.LocationPrecision);
            Assert.Null(remote.MapLatitude); // remote: coordinates are never kept
        }

        await SetAsync(id, new MapLocationInput(LocationPrecision.Unknown, null, null, "egal"));
        using var readDb = _database.CreateContext();
        var reset = await readDb.Applications.SingleAsync();
        Assert.Equal((LocationPrecision.Unknown, (double?)null, (string?)null), (reset.LocationPrecision, reset.MapLatitude, reset.MapLabel));
    }

    [Fact]
    public async Task SetLocation_MissingApplication_ReturnsNull()
    {
        Assert.Null(await SetAsync(999, new MapLocationInput(LocationPrecision.City, HamburgLat, HamburgLon, null)));
    }

    // ---------- Map response ----------

    [Fact]
    public void Map_DistancesOnlyForConfirmedPositions_WithOrigin()
    {
        var apps = new[]
        {
            App(1, "Berlin GmbH", LocationPrecision.City, BerlinLat, BerlinLon),
            App(2, "Unbekannt AG", location: "Irgendwo"),
            App(3, "Remote KG", LocationPrecision.Remote),
            App(4, "Kaputt", LocationPrecision.Unknown, BerlinLat, BerlinLon) // stale coordinates without confirmed precision
        };

        var withOrigin = MapService.BuildMap(apps, HamburgLat, HamburgLon);
        var withoutOrigin = MapService.BuildMap(apps, null, null);

        Assert.True(withOrigin.HasOrigin);
        Assert.InRange(withOrigin.Items.Single(i => i.Id == 1).DistanceKm!.Value, 254, 257);
        Assert.All(withOrigin.Items.Where(i => i.Id != 1), i => Assert.Null(i.DistanceKm)); // nothing invented
        Assert.All(withOrigin.Items.Where(i => i.Id != 1), i => Assert.Null(i.Latitude));
        Assert.All(withoutOrigin.Items, i => Assert.Null(i.DistanceKm));
    }

    [Fact]
    public void Map_SeveralApplicationsAtOnePosition_AllListedWithSharedCount()
    {
        var apps = new[]
        {
            App(1, "Konzern A", LocationPrecision.Exact, 53.544679, 9.966123),
            App(2, "Konzern B", LocationPrecision.Exact, 53.544679, 9.966123),
            App(3, "Andere", LocationPrecision.Exact, 53.6, 10.0)
        };

        var items = MapService.BuildMap(apps, null, null).Items;

        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { 2, 2, 1 }, items.OrderBy(i => i.Id).Select(i => i.SameLocationCount));
    }

    [Fact]
    public void Map_ExposesNoNotesOrDocuments()
    {
        Assert.DoesNotContain(typeof(MapItem).GetProperties(), p => p.Name is "Notes" or "Documents" or "JobUrl");
    }

    // ---------- Navigation links ----------

    [Fact]
    public void Links_UseCoordinates_AndDocumentedTravelModes()
    {
        var links = NavigationLinks.Build(HamburgLat, HamburgLon, BerlinLat, BerlinLon, "Berlin")!;

        Assert.Equal("https://www.google.com/maps/dir/?api=1&origin=53.5511%2C9.9937&destination=52.52%2C13.405&travelmode=transit", links.Transit);
        Assert.EndsWith("&travelmode=driving", links.Driving);
        Assert.EndsWith("&travelmode=bicycling", links.Bicycling);
        Assert.EndsWith("&travelmode=walking", links.Walking);
    }

    [Fact]
    public void Links_EncodeFreeText_SoNoParametersCanBeInjected()
    {
        var links = NavigationLinks.Build(null, null, null, null, "Müller & Söhne?x=1#frag Straße 5")!;

        Assert.DoesNotContain("origin=", links.Driving);
        Assert.Contains("destination=M%C3%BCller%20%26%20S%C3%B6hne%3Fx%3D1%23frag%20Stra%C3%9Fe%205&", links.Driving);
        Assert.Equal(3, links.Driving.Split('&').Length); // "…?api=1", "destination=…", "travelmode=…" – the "&" in the text added nothing
        Assert.True(Uri.TryCreate(links.Driving, UriKind.Absolute, out var uri) && uri.Host == "www.google.com");
    }

    [Fact]
    public void Links_FormatCoordinatesIndependentOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE"); // would write "53,5511"
            Assert.Contains("destination=53.5511%2C9.9937", NavigationLinks.Build(null, null, HamburgLat, HamburgLon, null)!.Walking);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Links_NoneForRemoteOrWithoutDestination()
    {
        Assert.Null(NavigationLinks.Build(HamburgLat, HamburgLon, null, null, "  "));
        var remote = MapService.BuildMap([App(1, "Remote KG", LocationPrecision.Remote, location: "Remote")], null, null).Items[0];
        Assert.Null(remote.Links);
    }

    // ---------- Geocoding (parsing only – no network in tests) ----------

    [Fact]
    public void NominatimResults_AreParsed_InvalidOnesSkipped()
    {
        using var json = JsonDocument.Parse("""
            [
              { "lat": "53.5446", "lon": "9.9661", "display_name": "Hafenstraße 12, Hamburg", "addresstype": "building" },
              { "lat": "53.5511", "lon": "9.9937", "display_name": "Hamburg, Deutschland", "addresstype": "city" },
              { "lat": "abc", "lon": "9.9", "display_name": "kaputt", "addresstype": "road" },
              { "lat": "53.55", "lon": "9.99", "display_name": "Altona", "addresstype": "suburb" }
            ]
            """);

        var results = NominatimGeocoder.Parse(json);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { LocationPrecision.Exact, LocationPrecision.City, LocationPrecision.Approximate },
            results.Select(r => r.SuggestedPrecision));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    public void GeocodeQuery_TooShort_IsRejected(string? query)
    {
        Assert.NotNull(NominatimGeocoder.ValidateQuery(query));
    }

    // ---------- Migration keeps existing data ----------

    [Fact]
    public void Migration_AddMapLocation_PreservesExistingApplications()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();

        migrator.Migrate("AddInterviews"); // state before this milestone
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO Applications (CompanyName, Position, Location, Status, Notes, CreatedAt, UpdatedAt, StatusChangedAt)
                VALUES ('Altfirma GmbH', 'Azubi', 'Kiel', 'Beworben', 'alte Notiz', '2026-09-01 10:00:00', '2026-09-01 10:00:00', '2026-09-01 10:00:00')
                """;
            insert.ExecuteNonQuery();
        }

        migrator.Migrate(); // apply AddMapLocation

        var app = db.Applications.Single();
        Assert.Equal(("Altfirma GmbH", "Kiel", "alte Notiz", ApplicationStatus.Beworben),
            (app.CompanyName, app.Location, app.Notes, app.Status));
        Assert.Equal(LocationPrecision.Unknown, app.LocationPrecision);
        Assert.Null(app.MapLatitude);
    }
}
