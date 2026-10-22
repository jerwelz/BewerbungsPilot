using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BewerbungsPilot.Tests;

// Endpoint authorization matrix (docs/security.md) against the real application over HTTP:
// anonymous access, other users' ids, aggregates, CSRF and caching headers.
public sealed class AuthorizationTests : IAsyncLifetime
{
    private readonly AppFactory _factory = new();
    private ApiSession _a = null!;
    private ApiSession _b = null!;
    private int _applicationId, _interviewId, _documentId;
    private static readonly byte[] Pdf = "%PDF-1.4\n% Geheimes Dokument von A\n"u8.ToArray();
    private static readonly string NextWeek = DateTime.Now.Date.AddDays(7).AddHours(14).ToString("yyyy-MM-ddTHH:mm:ss");

    public async Task InitializeAsync()
    {
        _a = await ApiSession.RegisterAsync(_factory, "anna@example.org");
        _b = await ApiSession.RegisterAsync(_factory, "ben@example.org");

        var created = await _a.SendAsync(HttpMethod.Post, "/api/applications", new
        {
            companyName = "Nordlicht GmbH", position = "Azubi", location = "Hamburg", deadline = DateTime.Now.Date.AddDays(10).ToString("yyyy-MM-dd"),
            appliedOn = DateTime.Now.Date.AddDays(-3).ToString("yyyy-MM-dd"), status = "Beworben", notes = "Geheime Notiz von A"
        });
        _applicationId = await IdAsync(created);
        Assert.Equal(HttpStatusCode.OK, (await _a.SendAsync(HttpMethod.Put, $"/api/applications/{_applicationId}/map-location",
            new { precision = "Exact", latitude = 53.5446, longitude = 9.9661, label = "Geheimer Ort von A" })).StatusCode);
        var interview = await _a.SendAsync(HttpMethod.Post, $"/api/applications/{_applicationId}/interviews",
            new { scheduledAt = NextWeek, type = "Video", contactPerson = "Kontakt von A", status = "Geplant" });
        _interviewId = await IdAsync(interview);
        var upload = await _a.SendAsync(HttpMethod.Post, $"/api/applications/{_applicationId}/documents", content: PdfUpload());
        _documentId = await IdAsync(upload);
    }

    private static async Task<int> IdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt32();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    private static MultipartFormDataContent PdfUpload()
    {
        var file = new ByteArrayContent(Pdf);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new MultipartFormDataContent { { file, "file", "Lebenslauf.pdf" }, { new StringContent("Lebenslauf"), "category" } };
    }

    // ---------- The matrix ----------

    public enum Kind { IdRead, IdWrite, Aggregate, Create, Geocode }

    public sealed record Route(string Method, string Template, Kind Kind)
    {
        public override string ToString() => $"{Method} {Template}";
    }

    public static readonly Route[] Routes =
    [
        new("GET", "/api/applications", Kind.Aggregate),
        new("GET", "/api/applications/{app}", Kind.IdRead),
        new("GET", "/api/applications/duplicates?companyName=Nordlicht%20GmbH&position=Azubi", Kind.Aggregate),
        new("POST", "/api/applications", Kind.Create),
        new("PUT", "/api/applications/{app}", Kind.IdWrite),
        new("PATCH", "/api/applications/{app}/status", Kind.IdWrite),
        new("DELETE", "/api/applications/{app}", Kind.IdWrite),
        new("GET", "/api/dashboard", Kind.Aggregate),
        new("GET", "/api/applications/{app}/documents", Kind.IdRead),
        new("POST", "/api/applications/{app}/documents", Kind.IdWrite),
        new("GET", "/api/documents/{document}/preview", Kind.IdRead),
        new("HEAD", "/api/documents/{document}/preview", Kind.IdRead),
        new("GET", "/api/documents/{document}/download", Kind.IdRead),
        new("DELETE", "/api/documents/{document}", Kind.IdWrite),
        new("POST", "/api/applications/{app}/interviews", Kind.IdWrite),
        new("PUT", "/api/interviews/{interview}", Kind.IdWrite),
        new("DELETE", "/api/interviews/{interview}", Kind.IdWrite),
        new("GET", "/api/calendar?from={from}&to={to}", Kind.Aggregate),
        new("GET", "/api/calendar/upcoming", Kind.Aggregate),
        new("GET", "/api/timeline", Kind.Aggregate),
        new("GET", "/api/waiting?sort=SinceSubmission", Kind.Aggregate),
        new("GET", "/api/analysis", Kind.Aggregate),
        new("POST", "/api/map/query", Kind.Aggregate),
        new("PUT", "/api/applications/{app}/map-location", Kind.IdWrite),
        new("POST", "/api/geocode", Kind.Geocode),
    ];

    public static TheoryData<Route> AllRoutes() => new(Routes);

    private Task<HttpResponseMessage> CallAsync(ApiSession session, Route route, string? csrfToken = null)
    {
        var url = route.Template
            .Replace("{app}", _applicationId.ToString()).Replace("{interview}", _interviewId.ToString())
            .Replace("{document}", _documentId.ToString())
            .Replace("{from}", DateTime.Now.Date.ToString("yyyy-MM-dd")).Replace("{to}", DateTime.Now.Date.AddDays(30).ToString("yyyy-MM-dd"));
        var (body, content) = (route.Method, route.Template) switch
        {
            ("POST", "/api/applications") or ("PUT", "/api/applications/{app}") =>
                ((object?)new { companyName = "Gehackt AG", position = "x", status = "Absage" }, (HttpContent?)null),
            ("PATCH", _) => (new { status = "Absage" }, null),
            ("POST", "/api/applications/{app}/documents") => (null, PdfUpload()),
            ("POST", "/api/applications/{app}/interviews") or ("PUT", "/api/interviews/{interview}") =>
                (new { scheduledAt = NextWeek, type = "Telefon", notes = "gehackt", status = "Geplant" }, null),
            ("POST", "/api/map/query") => (new { origin = new { latitude = 53.55, longitude = 9.99 } }, null),
            ("PUT", "/api/applications/{app}/map-location") => (new { precision = "City", latitude = 1.0, longitude = 1.0, label = "gehackt" }, null),
            ("POST", "/api/geocode") => (new { query = "Hamburg" }, null),
            _ => (null, null)
        };
        return session.SendAsync(new HttpMethod(route.Method), url, body, csrfToken, content);
    }

    // Everything A owns, as A sees it – used to prove "unchanged".
    private async Task<string> SnapshotOfAAsync()
    {
        var detail = await _a.GetJsonAsync($"/api/applications/{_applicationId}");
        var download = await _a.SendAsync(HttpMethod.Get, $"/api/documents/{_documentId}/download");
        var files = Directory.GetFiles(Path.Combine(_factory.DataDirectory, "documents")).Length;
        return $"{detail.GetRawText()}|{Convert.ToHexString(SHA256.HashData(await download.Content.ReadAsByteArrayAsync()))}|{files}";
    }

    private static readonly string[] SecretsOfA = ["Nordlicht", "Geheime Notiz von A", "Geheimer Ort von A", "Kontakt von A", "Geheimes Dokument"];

    // ---------- Data directory isolation (protects the real storage/ folder) ----------

    [Fact]
    public void TestHost_UsesItsOwnTemporaryDataDirectory()
    {
        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, "bewerbungspilot.db")));
    }

    // ---------- Anonymous ----------

    [Theory]
    [MemberData(nameof(AllRoutes))]
    public async Task Anonymous_Gets401_AndNoData(Route route)
    {
        var anonymous = new ApiSession(_factory);
        await anonymous.RefreshTokenAsync(); // even with a valid anonymous CSRF token
        var response = await CallAsync(anonymous, route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.All(SecretsOfA, secret => Assert.DoesNotContain(secret, body));
    }

    // ---------- Another user's ids (IDOR) ----------

    [Fact]
    public async Task OtherUser_WithForeignIds_Gets404_AndChangesNothing()
    {
        var before = await SnapshotOfAAsync();
        foreach (var route in Routes.Where(r => r.Kind is Kind.IdRead or Kind.IdWrite))
        {
            var response = await CallAsync(_b, route);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{route} → {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.All(SecretsOfA, secret => Assert.DoesNotContain(secret, body));
            Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal(before, await SnapshotOfAAsync());
    }

    // ---------- Aggregates ----------

    [Fact]
    public async Task OtherUser_Aggregates_ContainNoForeignData()
    {
        await _b.SendAsync(HttpMethod.Post, "/api/applications", new { companyName = "Hafen Logistik AG", position = "Azubi", status = "Beworben" });
        foreach (var route in Routes.Where(r => r.Kind == Kind.Aggregate))
        {
            var response = await CallAsync(_b, route);
            Assert.True(response.IsSuccessStatusCode, $"{route} → {(int)response.StatusCode}");
            var body = await response.Content.ReadAsStringAsync();
            Assert.All(SecretsOfA, secret => Assert.DoesNotContain(secret, body));
        }
        Assert.Equal(1, (await _b.GetJsonAsync("/api/dashboard")).GetProperty("total").GetInt32());
        Assert.Contains("Nordlicht", (await _a.GetJsonAsync("/api/dashboard")).GetRawText()); // the owner still sees it
    }

    [Fact]
    public async Task CreatedRecords_BelongToTheCaller_EvenIfTheBodySaysOtherwise()
    {
        var response = await _b.SendAsync(HttpMethod.Post, "/api/applications",
            new { companyName = "Hafen Logistik AG", position = "Azubi", status = "Beworben", ownerId = _a.UserId });
        var id = await IdAsync(response);
        Assert.Equal(HttpStatusCode.OK, (await _b.SendAsync(HttpMethod.Get, $"/api/applications/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _a.SendAsync(HttpMethod.Get, $"/api/applications/{id}")).StatusCode);
    }

    // ---------- Owner ----------

    [Fact]
    public async Task Owner_CanPreviewAndDownloadOwnDocument()
    {
        foreach (var url in new[] { $"/api/documents/{_documentId}/preview", $"/api/documents/{_documentId}/download" })
        {
            var response = await _a.SendAsync(HttpMethod.Get, url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Pdf, await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
    }

    [Fact]
    public async Task DocumentFiles_AreNotReachableAsStaticFiles()
    {
        var stored = Path.GetFileName(Directory.GetFiles(Path.Combine(_factory.DataDirectory, "documents")).Single());
        foreach (var url in new[] { $"/{stored}", $"/documents/{stored}", $"/storage/documents/{stored}", "/api/documents/..%2F..%2Fbewerbungspilot.db/download" })
        {
            var response = await _a.SendAsync(HttpMethod.Get, url);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ---------- CSRF ----------

    [Fact]
    public async Task UnsafeRequests_WithoutValidCsrfToken_Get403_AndChangeNothing()
    {
        var before = await SnapshotOfAAsync();
        var unsafeRoutes = Routes.Where(r => r.Method is "POST" or "PUT" or "PATCH" or "DELETE");
        foreach (var route in unsafeRoutes)
        {
            foreach (var token in new[] { "", "ungueltiges-token", _b.Token! }) // none, garbage, another user's token
            {
                var response = await CallAsync(_a, route, token);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{route} with token '{token[..Math.Min(8, token.Length)]}' → {(int)response.StatusCode}");
            }
        }
        Assert.Equal(before, await SnapshotOfAAsync());
        Assert.Equal(1, (await _a.GetJsonAsync("/api/dashboard")).GetProperty("total").GetInt32()); // nothing was created
    }

    // ---------- Caching and security headers ----------

    [Fact]
    public async Task ApiAndHtml_AreNeverCached_AndCarrySecurityHeaders()
    {
        foreach (var url in new[] { "/api/applications", $"/api/applications/{_applicationId}", "/api/dashboard", "/api/auth/me", "/", "/bewerbungen.html", "/login.html" })
        {
            var response = await _a.SendAsync(HttpMethod.Get, url);
            Assert.True(response.IsSuccessStatusCode, $"{url} → {(int)response.StatusCode}");
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        }
        var anonymous = await new ApiSession(_factory).SendAsync(HttpMethod.Get, "/api/applications");
        Assert.Contains("no-store", anonymous.Headers.CacheControl?.ToString() ?? "");
    }

    // ---------- Every API endpoint is covered by this matrix ----------

    [Fact]
    public void EveryApiEndpoint_IsInTheAuthorizationMatrix()
    {
        static string Normalize(string route) => System.Text.RegularExpressions.Regex.Replace(route.Split('?')[0].TrimEnd('/'), @"\{[^}]+\}", "{}");

        var actual = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.TrimStart('/').StartsWith("api"))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => $"{m} {Normalize("/" + e.RoutePattern.RawText!.TrimStart('/'))}"))
            .ToHashSet();
        var expected = Routes.Select(r => $"{r.Method} {Normalize(r.Template)}")
            .Concat(AuthTests.AuthRoutes.Select(r => $"{r.Split(' ')[0]} {Normalize(r.Split(' ')[1])}"))
            .ToHashSet();

        Assert.Empty(actual.Except(expected)); // an endpoint without authorization tests
        Assert.Empty(expected.Except(actual)); // a test for an endpoint that no longer exists
    }
}
