using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BewerbungsPilot.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BewerbungsPilot.Tests;

// Registration, login, logout, lockout, rate limiting, password change and session expiration.
public sealed class AuthTests : IDisposable
{
    public static readonly string[] AuthRoutes =
    [
        "GET /api/auth/me", "GET /api/auth/antiforgery", "POST /api/auth/register",
        "POST /api/auth/login", "POST /api/auth/logout", "POST /api/auth/password",
    ];

    private readonly List<AppFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
            factory.Dispose();
    }

    private AppFactory Factory(Dictionary<string, string?>? settings = null, TimeProvider? clock = null)
    {
        var factory = new AppFactory(settings, clock);
        _factories.Add(factory);
        return factory;
    }

    private static async Task<string> TitleAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").Deserialize<Dictionary<string, string[]>>()!;

    [Fact]
    public async Task Register_Me_Logout_FullCycle()
    {
        var factory = Factory();
        var session = await ApiSession.RegisterAsync(factory, "anna@example.org");
        var me = await session.GetJsonAsync("/api/auth/me");
        Assert.Equal("anna@example.org", me.GetProperty("email").GetString());

        var stolen = new Dictionary<string, string>(session.Cookies); // a copy of the login cookie
        Assert.Equal(HttpStatusCode.NoContent, (await session.SendAsync(HttpMethod.Post, "/api/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.SendAsync(HttpMethod.Get, "/api/auth/me")).StatusCode);

        // Logout ends the session on the server too: the old cookie no longer works.
        var replay = new ApiSession(factory);
        foreach (var (name, value) in stolen)
            replay.Cookies[name] = value;
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);
    }

    [Fact]
    public async Task LoginCookie_IsHttpOnly_SameSiteStrict_AndSessionOnlyByDefault()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);
        await session.LoginAsync("anna@example.org");
        await session.RefreshTokenAsync();

        var login = new ApiSession(factory);
        await login.RefreshTokenAsync();
        await login.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "anna@example.org", password = ApiSession.Password, rememberMe = false });
        var cookie = login.LastSetCookies.Single(c => c.StartsWith("BewerbungsPilot.Auth="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);

        var remember = new ApiSession(factory);
        await remember.RefreshTokenAsync();
        await remember.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "anna@example.org", password = ApiSession.Password, rememberMe = true });
        Assert.Contains("expires=", remember.LastSetCookies.Single(c => c.StartsWith("BewerbungsPilot.Auth=")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownUser_GiveTheSameAnswer()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);

        var wrong = await session.LoginAsync("anna@example.org", "falsches-passwort-123");
        var unknown = await session.LoginAsync("niemand@example.org", "falsches-passwort-123");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(await TitleAsync(wrong), await TitleAsync(unknown));
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.SendAsync(HttpMethod.Get, "/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_FiveFailures_LockTheAccount()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);
        for (var i = 0; i < 5; i++)
            await session.LoginAsync("anna@example.org", "falsches-passwort-123");

        var correct = await session.LoginAsync("anna@example.org");
        Assert.Equal(HttpStatusCode.BadRequest, correct.StatusCode);
        Assert.Contains("gesperrt", await TitleAsync(correct));
    }

    [Fact]
    public async Task Login_TooManyRequests_AreRateLimited()
    {
        var factory = Factory(new() { ["Auth:RequestsPerMinute"] = "3" });
        var session = new ApiSession(factory);
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            statuses.Add((await session.LoginAsync($"x{i}@example.org", "irgendein-passwort")).StatusCode);
        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task Register_ValidatesInput_InGerman()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);
        await session.RefreshTokenAsync();

        var invalidEmail = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "keine-adresse", password = ApiSession.Password });
        Assert.Contains("gültige E-Mail", (await ErrorsAsync(invalidEmail))["email"][0]);
        var shortPassword = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "neu@example.org", password = "kurz" });
        Assert.Contains("mindestens 12 Zeichen", (await ErrorsAsync(shortPassword))["password"][0]);
        var duplicate = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "ANNA@example.org", password = ApiSession.Password });
        Assert.Contains("bereits ein Konto", (await ErrorsAsync(duplicate))["email"][0]);
    }

    [Fact]
    public async Task Register_CanBeDisabled()
    {
        var factory = Factory(new() { ["Auth:AllowRegistration"] = "false" });
        var session = new ApiSession(factory);
        await session.RefreshTokenAsync();
        var response = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "anna@example.org", password = ApiSession.Password });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((await session.GetJsonAsync("/api/auth/antiforgery")).GetProperty("allowRegistration").GetBoolean());
    }

    [Fact]
    public async Task LoginAndRegister_RequireTheCsrfToken()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);
        var login = await session.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "anna@example.org", password = ApiSession.Password }, csrfToken: "");
        var register = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "neu@example.org", password = ApiSession.Password }, csrfToken: "");
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);
    }

    [Fact]
    public async Task AnonymousCsrfToken_IsInvalidAfterLogin()
    {
        var factory = Factory();
        await ApiSession.RegisterAsync(factory, "anna@example.org");
        var session = new ApiSession(factory);
        await session.RefreshTokenAsync();
        var anonymousToken = session.Token;
        await session.SendAsync(HttpMethod.Post, "/api/auth/login", new { email = "anna@example.org", password = ApiSession.Password });

        var response = await session.SendAsync(HttpMethod.Post, "/api/applications",
            new { companyName = "x", position = "x", status = "Entwurf" }, csrfToken: anonymousToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PasswordChange_EndsOtherSessions_AndKeepsThisOne()
    {
        var factory = Factory();
        var first = await ApiSession.RegisterAsync(factory, "anna@example.org");
        var second = new ApiSession(factory);
        Assert.True((await second.LoginAsync("anna@example.org")).IsSuccessStatusCode);

        var wrong = await first.SendAsync(HttpMethod.Post, "/api/auth/password", new { currentPassword = "falsch-falsch-falsch", newPassword = "ein-neues-langes-passwort" });
        Assert.Contains("aktuelle Passwort", (await ErrorsAsync(wrong))["currentPassword"][0]);

        var changed = await first.SendAsync(HttpMethod.Post, "/api/auth/password", new { currentPassword = ApiSession.Password, newPassword = "ein-neues-langes-passwort" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.SendAsync(HttpMethod.Get, "/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.SendAsync(HttpMethod.Get, "/api/auth/me")).StatusCode);

        var third = new ApiSession(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await third.LoginAsync("anna@example.org")).StatusCode);
        Assert.True((await third.LoginAsync("anna@example.org", "ein-neues-langes-passwort")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Session_ExpiresAfterInactivity_ButSlidesWhileActive()
    {
        var clock = new MutableClock();
        var factory = Factory(clock: clock);
        var session = await ApiSession.RegisterAsync(factory, "anna@example.org");

        clock.Advance(TimeSpan.FromHours(5));
        Assert.Equal(HttpStatusCode.OK, (await session.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);
        clock.Advance(TimeSpan.FromHours(5)); // 10 h after login, but only 5 h after the last request
        Assert.Equal(HttpStatusCode.OK, (await session.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);
        clock.Advance(TimeSpan.FromHours(9)); // inactive longer than 8 h
        Assert.Equal(HttpStatusCode.Unauthorized, (await session.SendAsync(HttpMethod.Get, "/api/applications")).StatusCode);
    }

    [Fact]
    public async Task ExistingDatabaseWithPendingMigration_PreventsStartup()
    {
        var factory = Factory();
        // An M8 database (before user accounts) in the data directory.
        using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                   .UseSqlite($"Data Source={Path.Combine(factory.DataDirectory, "bewerbungspilot.db")}").Options))
            db.GetService<IMigrator>().Migrate("20261022151700_AddMapLocation");

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        using var check = new SqliteConnection($"Data Source={Path.Combine(factory.DataDirectory, "bewerbungspilot.db")};Pooling=False");
        check.Open();
        using var command = check.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        Assert.Equal("20261022151700_AddMapLocation", command.ExecuteScalar()); // not migrated
    }
}
