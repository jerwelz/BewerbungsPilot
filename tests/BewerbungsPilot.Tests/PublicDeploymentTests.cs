using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BewerbungsPilot.Logic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BewerbungsPilot.Tests;

// Milestone 11: the demo as a public website (Production environment behind a proxy).
public class PublicDeploymentTests : IDisposable
{
    private const string Host = "https://demo.example.org";
    private readonly List<AppFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
    }

    private AppFactory Factory(Dictionary<string, string?>? settings = null, TimeProvider? clock = null,
        string environment = "Development", string? dataDirectory = null)
    {
        var factory = new AppFactory(settings, clock, environment, dataDirectory);
        _factories.Add(factory);
        return factory;
    }

    // The configuration of the public instance (fly.toml), with test host names.
    private static Dictionary<string, string?> PublicDemoSettings(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Demo:Enabled"] = "true",
            ["AllowedHosts"] = "demo.example.org;localhost",
            ["https_port"] = "443", // the app itself only listens on HTTP behind the proxy (ASPNETCORE_HTTPS_PORT)
        };
        foreach (var (key, value) in extra ?? [])
            settings[key] = value;
        return settings;
    }

    private AppFactory PublicDemo(Dictionary<string, string?>? extra = null, TimeProvider? clock = null) =>
        Factory(PublicDemoSettings(extra), clock, "Production");

    private static async Task<ApiSession> StartDemoAsync(AppFactory factory, string? baseAddress = null)
    {
        var session = new ApiSession(factory, baseAddress);
        await session.RefreshTokenAsync();
        var response = await session.SendAsync(HttpMethod.Post, "/api/demo/start");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await session.RefreshTokenAsync();
        return session;
    }

    // ---------- Landing page and health check ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LandingPageAndHealthCheck_AreReachableWithoutLogin(bool demo)
    {
        var factory = Factory(demo ? new() { ["Demo:Enabled"] = "true" } : null);
        var anonymous = new ApiSession(factory);

        var landing = await anonymous.SendAsync(HttpMethod.Get, "/willkommen.html");
        Assert.Equal(HttpStatusCode.OK, landing.StatusCode);
        var html = await landing.Content.ReadAsStringAsync();
        Assert.Contains("Demo starten", html);
        Assert.Contains("Anmelden", html);
        Assert.DoesNotContain("registrieren", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.SendAsync(HttpMethod.Get, "/img/01-uebersicht.png")).StatusCode);

        var health = await anonymous.SendAsync(HttpMethod.Get, "/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("Healthy", await health.Content.ReadAsStringAsync());
    }

    // ---------- Production configuration ----------

    [Theory]
    [InlineData("demo.example.org", "/data/demo", true, null)]
    [InlineData("demo.example.org", "storage-demo", true, "absolutes Datenverzeichnis")]
    [InlineData("*", "/data/demo", true, "AllowedHosts")]
    [InlineData(null, "/data/demo", true, "AllowedHosts")]
    [InlineData("*", "storage-demo", false, null)] // Development (local demo): no requirement
    public void PublicDemo_NeedsHostNameAndAbsoluteDataDirectory(string? hosts, string dataDirectory, bool production, string? problem)
    {
        if (OperatingSystem.IsWindows() && dataDirectory.StartsWith('/'))
            dataDirectory = @"C:\data\demo"; // "absolute" depends on the operating system
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = hosts,
            ["Storage:DataDirectory"] = dataDirectory,
        }).Build();

        var result = DemoAccounts.ProductionConfigurationProblem(configuration, isDevelopment: !production);
        if (problem is null)
            Assert.Null(result);
        else
            Assert.Contains(problem, result);
    }

    [Fact]
    public async Task Production_RedirectsToHttps_ExceptForTheHealthCheck()
    {
        var factory = PublicDemo();
        var http = new ApiSession(factory, "http://demo.example.org");

        var page = await http.SendAsync(HttpMethod.Get, "/willkommen.html");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, page.StatusCode);
        Assert.StartsWith("https://demo.example.org/", page.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(HttpMethod.Get, "/health")).StatusCode);
    }

    [Fact]
    public async Task Production_SendsSecurityHeaders_AndSecureHostOnlyCookies()
    {
        var factory = PublicDemo();
        var visitor = new ApiSession(factory, Host);

        var page = await visitor.SendAsync(HttpMethod.Get, "/willkommen.html");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("max-age=", page.Headers.GetValues("Strict-Transport-Security").Single());
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.Contains("frame-ancestors 'self'", csp);
        Assert.Contains("geolocation=()", page.Headers.GetValues("Permissions-Policy").Single());

        await visitor.RefreshTokenAsync();
        var antiforgery = visitor.LastSetCookies.Single();
        Assert.StartsWith("__Host-BewerbungsPilot.Antiforgery=", antiforgery);
        Assert.Contains("secure", antiforgery);
        Assert.Contains("samesite=strict", antiforgery);

        var start = await visitor.SendAsync(HttpMethod.Post, "/api/demo/start");
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var login = visitor.LastSetCookies.Single(c => c.StartsWith("__Host-BewerbungsPilot="));
        Assert.Contains("secure", login);
        Assert.Contains("httponly", login);
        Assert.Contains("samesite=strict", login);
    }

    [Fact]
    public async Task Production_RejectsUnknownHostNames()
    {
        var factory = PublicDemo();
        var response = await new ApiSession(factory, "https://fremde-seite.example").SendAsync(HttpMethod.Get, "/willkommen.html");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Production_RegistrationIsClosed_EvenWhenConfiguredOpen()
    {
        var factory = PublicDemo(new() { ["Auth:AllowRegistration"] = "true" });
        var visitor = new ApiSession(factory, Host);
        var options = await visitor.GetJsonAsync("/api/auth/antiforgery");
        Assert.False(options.GetProperty("allowRegistration").GetBoolean());
        Assert.True(options.GetProperty("demoEnabled").GetBoolean());

        await visitor.RefreshTokenAsync();
        var register = await visitor.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "echt@example.org", password = ApiSession.Password });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
    }

    [Fact]
    public async Task Production_UnexpectedErrors_AnswerGermanProblemDetails_WithoutInternals()
    {
        var clock = new FailingClock();
        var factory = PublicDemo(clock: clock);
        var demo = await StartDemoAsync(factory, Host);

        clock.Fail = true;
        var response = await demo.SendAsync(HttpMethod.Get, "/api/dashboard");
        clock.Fail = false;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Ein unerwarteter Fehler ist aufgetreten", JsonDocument.Parse(body).RootElement.GetProperty("title").GetString());
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain("BewerbungsPilot.", body);
    }

    // ---------- Reverse proxy (Fly.io) ----------

    [Fact]
    public async Task BehindTheProxy_TheForwardedProtocolAndClientAddressAreUsed()
    {
        var factory = PublicDemo(new()
        {
            ["ReverseProxy:TrustAllProxies"] = "true",
            ["ReverseProxy:ClientIpHeader"] = "Fly-Client-IP",
            ["Demo:StartsPerHour"] = "1",
        });

        // The proxy terminates HTTPS and calls the app over HTTP: no redirect loop.
        var first = new ApiSession(factory, "http://demo.example.org");
        first.Headers["X-Forwarded-Proto"] = "https";
        first.Headers["Fly-Client-IP"] = "203.0.113.10";
        Assert.Equal(HttpStatusCode.OK, (await first.SendAsync(HttpMethod.Get, "/willkommen.html")).StatusCode);

        // The demo-start limit counts per visitor address, not for the proxy as a whole.
        await first.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await first.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);
        var again = new ApiSession(factory, "http://demo.example.org");
        again.Headers["X-Forwarded-Proto"] = "https";
        again.Headers["Fly-Client-IP"] = "203.0.113.10";
        await again.RefreshTokenAsync();
        var limited = await again.SendAsync(HttpMethod.Post, "/api/demo/start");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("zu viele Demos", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        var other = new ApiSession(factory, "http://demo.example.org");
        other.Headers["X-Forwarded-Proto"] = "https";
        other.Headers["Fly-Client-IP"] = "198.51.100.20";
        await other.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await other.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);
    }

    [Fact]
    public async Task WithoutProxyConfiguration_ForwardedHeadersAreIgnored()
    {
        var factory = PublicDemo();
        var spoofed = new ApiSession(factory, "http://demo.example.org");
        spoofed.Headers["X-Forwarded-Proto"] = "https";
        spoofed.Headers["Fly-Client-IP"] = "203.0.113.10";
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await spoofed.SendAsync(HttpMethod.Get, "/willkommen.html")).StatusCode);
    }

    // ---------- Demo limits ----------

    [Fact]
    public async Task DemoStart_HasItsOwnRateLimit_LoginIsNotAffected()
    {
        var factory = Factory(new() { ["Demo:Enabled"] = "true", ["Demo:StartsPerHour"] = "2" });
        await StartDemoAsync(factory);
        await StartDemoAsync(factory);

        var third = new ApiSession(factory);
        await third.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await third.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);

        // The login endpoint still answers normally (wrong data → 400, not 429).
        Assert.Equal(HttpStatusCode.BadRequest, (await third.LoginAsync("niemand@example.org")).StatusCode);
    }

    [Fact]
    public async Task DemoVisitor_ChangesAreRateLimited()
    {
        var factory = Factory(new() { ["Demo:Enabled"] = "true", ["Demo:WritesPerMinute"] = "3" });
        var demo = await StartDemoAsync(factory);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await demo.SendAsync(HttpMethod.Post, "/api/applications",
                new { companyName = $"Testfirma {i}", position = "Azubi", status = "Entwurf" })).StatusCode);

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, (await demo.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode); // reading stays possible
    }

    [Fact]
    public async Task DemoAccount_HasAnUpperLimitOfApplications()
    {
        var factory = Factory(new() { ["Demo:Enabled"] = "true" });
        var demo = await StartDemoAsync(factory);
        var existing = (await demo.GetJsonAsync("/api/applications")).GetArrayLength();
        for (var i = existing; i < DemoAccounts.MaxApplicationsPerAccount; i++)
            Assert.Equal(HttpStatusCode.Created, (await demo.SendAsync(HttpMethod.Post, "/api/applications",
                new { companyName = $"Testfirma {i}", position = "Azubi", status = "Entwurf" })).StatusCode);

        var tooMany = await demo.SendAsync(HttpMethod.Post, "/api/applications", new { companyName = "Eine zu viel", position = "Azubi", status = "Entwurf" });
        Assert.Equal(HttpStatusCode.Forbidden, tooMany.StatusCode);
        Assert.Contains("höchstens 50 Bewerbungen", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());

        // Another visitor has their own limit.
        var other = await StartDemoAsync(factory);
        Assert.Equal(HttpStatusCode.Created, (await other.SendAsync(HttpMethod.Post, "/api/applications",
            new { companyName = "Andere Firma", position = "Azubi", status = "Entwurf" })).StatusCode);
    }

    [Fact]
    public async Task LongTexts_AreRejected()
    {
        var factory = Factory();
        var user = await ApiSession.RegisterAsync(factory, "anna@example.org");
        var response = await user.SendAsync(HttpMethod.Post, "/api/applications", new
        {
            companyName = "Nordlicht GmbH", position = "Azubi", status = "Entwurf",
            notes = new string('x', ApplicationService.MaxNotesLength + 1),
            location = new string('y', 201),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("notes", out _));
        Assert.True(errors.TryGetProperty("location", out _));
    }

    // ---------- Expiry without a new visitor ----------

    [Fact]
    public async Task CleanupService_DeletesExpiredDemoAccounts_AndEndsTheirSessions()
    {
        var clock = new MutableClock();
        var factory = Factory(new() { ["Demo:Enabled"] = "true" }, clock);
        var demo = await StartDemoAsync(factory);
        var cleanup = factory.Services.GetServices<IHostedService>().OfType<DemoCleanupService>().Single();

        Assert.Equal(0, await cleanup.RunOnceAsync()); // still young
        Assert.Equal(HttpStatusCode.OK, (await demo.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);

        clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));
        Assert.Equal(1, await cleanup.RunOnceAsync());
        Assert.Empty(Directory.GetFiles(Path.Combine(factory.DataDirectory, "documents")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await demo.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);
    }

    [Fact]
    public void CleanupService_RunsOnlyInDemoMode()
    {
        var factory = Factory();
        Assert.Empty(factory.Services.GetServices<IHostedService>().OfType<DemoCleanupService>());
    }

    // ---------- Restart ----------

    [Fact]
    public async Task DataAndLogins_SurviveARestart()
    {
        var dataDirectory = AppFactory.NewDataDirectory();
        try
        {
            Dictionary<string, string> cookies;
            using (var first = new AppFactory(dataDirectory: dataDirectory))
            {
                var user = await ApiSession.RegisterAsync(first, "anna@example.org");
                Assert.Equal(HttpStatusCode.Created, (await user.SendAsync(HttpMethod.Post, "/api/applications",
                    new { companyName = "Nordlicht GmbH", position = "Azubi", status = "Beworben", appliedOn = "2026-01-15" })).StatusCode);
                cookies = new(user.Cookies);
            }

            using var second = new AppFactory(dataDirectory: dataDirectory);
            var sameBrowser = new ApiSession(second);
            foreach (var (name, value) in cookies)
                sameBrowser.Cookies[name] = value;

            // The login cookie of the first instance is still valid: the keys were persisted.
            Assert.Equal("anna@example.org", (await sameBrowser.GetJsonAsync("/api/auth/me")).GetProperty("email").GetString());
            var applications = await sameBrowser.GetJsonAsync("/api/applications");
            Assert.Equal("Nordlicht GmbH", applications.EnumerateArray().Single().GetProperty("companyName").GetString());
        }
        finally
        {
            AppFactory.DeleteDataDirectory(dataDirectory);
        }
    }

    // A clock that can be switched to fail, to provoke an unexpected server error.
    private sealed class FailingClock : TimeProvider
    {
        public bool Fail { get; set; }
        public override DateTimeOffset GetUtcNow() => Fail ? throw new InvalidOperationException("Testfehler") : DateTimeOffset.UtcNow;
    }
}
