using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Demo mode (Demo:Enabled): every visitor gets an isolated throwaway account with fictional data.
public class DemoTests : IDisposable
{
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

    private AppFactory DemoFactory(TimeProvider? clock = null, string? maxAccounts = null)
    {
        var settings = new Dictionary<string, string?> { ["Demo:Enabled"] = "true" };
        if (maxAccounts is not null)
            settings["Demo:MaxAccounts"] = maxAccounts;
        return Factory(settings, clock);
    }

    private static async Task<ApiSession> StartDemoAsync(AppFactory factory)
    {
        var session = new ApiSession(factory);
        await session.RefreshTokenAsync();
        var response = await session.SendAsync(HttpMethod.Post, "/api/demo/start");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await session.RefreshTokenAsync(); // the token is bound to the user
        return session;
    }

    // ---------- Off by default ----------

    [Fact]
    public async Task DemoEndpoint_DoesNotExist_WhenDemoModeIsOff()
    {
        var factory = Factory();
        var anonymous = new ApiSession(factory);
        await anonymous.RefreshTokenAsync();
        // Not mapped: the login-required fallback answers before any demo code could run.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);
        Assert.False((await anonymous.GetJsonAsync("/api/auth/antiforgery")).GetProperty("demoEnabled").GetBoolean());

        var user = await ApiSession.RegisterAsync(factory, "echt@example.org");
        Assert.Equal(HttpStatusCode.NotFound, (await user.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);
        Assert.False((await user.GetJsonAsync("/api/auth/me")).GetProperty("isDemo").GetBoolean());
    }

    // ---------- Starting a demo ----------

    [Fact]
    public async Task StartDemo_CreatesAccountWithFictionalData_AndSignsIn()
    {
        var factory = DemoFactory();
        var anonymous = new ApiSession(factory);
        Assert.True((await anonymous.GetJsonAsync("/api/auth/antiforgery")).GetProperty("demoEnabled").GetBoolean());

        var demo = await StartDemoAsync(factory);

        var me = await demo.GetJsonAsync("/api/auth/me");
        Assert.True(me.GetProperty("isDemo").GetBoolean());
        Assert.EndsWith("@demo.invalid", me.GetProperty("email").GetString());

        var applications = (await demo.GetJsonAsync("/api/applications")).EnumerateArray().ToList();
        Assert.Equal(12, applications.Count);
        Assert.Equal(Enum.GetNames<ApplicationStatus>().Order(),
            applications.Select(a => a.GetProperty("status").GetString()!).Distinct().Order());
        Assert.NotEmpty((await demo.GetJsonAsync("/api/calendar/upcoming")).EnumerateArray());
    }

    [Fact]
    public async Task StartDemo_NeedsTheAntiforgeryToken()
    {
        var session = new ApiSession(DemoFactory());
        await session.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await session.SendAsync(HttpMethod.Post, "/api/demo/start", csrfToken: "")).StatusCode);
    }

    [Fact]
    public async Task TwoDemoVisitors_CannotSeeEachOthersData()
    {
        var factory = DemoFactory();
        var first = await StartDemoAsync(factory);
        var second = await StartDemoAsync(factory);

        var firstIds = (await first.GetJsonAsync("/api/applications")).EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ToList();
        var secondIds = (await second.GetJsonAsync("/api/applications")).EnumerateArray().Select(a => a.GetProperty("id").GetInt32()).ToList();
        Assert.Empty(firstIds.Intersect(secondIds));

        Assert.Equal(HttpStatusCode.NotFound, (await second.SendAsync(HttpMethod.Get, $"/api/applications/{firstIds[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.SendAsync(HttpMethod.Delete, $"/api/applications/{firstIds[0]}")).StatusCode);
        Assert.Equal(12, (await first.GetJsonAsync("/api/applications")).GetArrayLength());
    }

    [Fact]
    public async Task DemoMode_BlocksRegistrationUploadsAndPlaceSearch()
    {
        var factory = DemoFactory(); // AppFactory switches registration on – demo mode must still refuse it
        var anonymous = new ApiSession(factory);
        await anonymous.RefreshTokenAsync();
        Assert.False((await anonymous.GetJsonAsync("/api/auth/antiforgery")).GetProperty("allowRegistration").GetBoolean());
        var register = await anonymous.SendAsync(HttpMethod.Post, "/api/auth/register", new { email = "echt@example.org", password = ApiSession.Password });
        Assert.Equal(HttpStatusCode.Forbidden, register.StatusCode);

        var demo = await StartDemoAsync(factory);
        var id = (await demo.GetJsonAsync("/api/applications"))[0].GetProperty("id").GetInt32();
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("%PDF-1.4\n%%EOF\n"u8.ToArray()), "file", "eigene.pdf" },
            { new StringContent("Lebenslauf"), "category" }
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.SendAsync(HttpMethod.Post, $"/api/applications/{id}/documents", content: form)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await demo.SendAsync(HttpMethod.Post, "/api/geocode", new { query = "Hamburg" })).StatusCode);
    }

    [Fact]
    public async Task DemoAccount_HasNoPassword_SoNobodyCanLogInToIt()
    {
        var factory = DemoFactory();
        var demo = await StartDemoAsync(factory);
        var email = (await demo.GetJsonAsync("/api/auth/me")).GetProperty("email").GetString()!;

        var login = await new ApiSession(factory).LoginAsync(email, "");
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        login = await new ApiSession(factory).LoginAsync(email, ApiSession.Password);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    // ---------- Limits and cleanup ----------

    [Fact]
    public async Task StartDemo_RefusesNewAccounts_WhenTheLimitIsReached()
    {
        var factory = DemoFactory(maxAccounts: "1");
        await StartDemoAsync(factory);
        var second = new ApiSession(factory);
        await second.RefreshTokenAsync();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await second.SendAsync(HttpMethod.Post, "/api/demo/start")).StatusCode);
    }

    [Fact]
    public async Task ExpiredDemoAccounts_AreDeletedWithTheirFiles_WhenTheNextDemoStarts()
    {
        var clock = new MutableClock();
        var factory = DemoFactory(clock);
        var old = await StartDemoAsync(factory);
        var documents = Path.Combine(factory.DataDirectory, "documents");
        Assert.Equal(2, Directory.GetFiles(documents).Length);

        clock.Advance(TimeSpan.FromHours(5)); // lifetime: 4 hours
        var fresh = await StartDemoAsync(factory);

        Assert.Equal(HttpStatusCode.Unauthorized, (await old.SendAsync(HttpMethod.Get, "/api/auth/me")).StatusCode);
        Assert.Equal(2, Directory.GetFiles(documents).Length); // only the new account's files
        Assert.Equal(12, (await fresh.GetJsonAsync("/api/applications")).GetArrayLength());
    }

    // ---------- Startup safeguards ----------

    [Theory]
    [InlineData("storage", true)]
    [InlineData("storage/", true)]
    [InlineData("./storage", true)]
    [InlineData("STORAGE", true)]
    [InlineData("storage-demo", false)]
    [InlineData("storage/../storage-demo", false)]
    public void DemoMode_RecognizesTheDefaultDataDirectory(string dataDirectory, bool isDefault)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "BewerbungsPilot.Tests", "content-root");
        Assert.Equal(isDefault, DemoAccounts.IsDefaultDataDirectory(contentRoot, dataDirectory));
        Assert.True(DemoAccounts.IsDefaultDataDirectory(contentRoot, Path.Combine(contentRoot, "storage")));
    }

    [Fact]
    public async Task DemoMode_DoesNotStart_OnADatabaseWithARealAccount()
    {
        var real = Factory();
        await ApiSession.RegisterAsync(real, "echt@example.org");

        var demo = Factory(new() { ["Demo:Enabled"] = "true", ["Storage:DataDirectory"] = real.DataDirectory });
        Assert.ThrowsAny<Exception>(() => demo.CreateClient());
    }

    // ---------- Seed data ----------

    [Fact]
    public async Task Seeder_CreatesConsistentFictionalData_ForTheCurrentUserOnly()
    {
        using var database = new TestDatabase();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        await using (var db = database.CreateContext())
            await new DemoSeeder(db, database.Storage, clock).SeedAsync();

        await using var a = database.CreateContext();
        var applications = await a.Applications.Include(x => x.Events).Include(x => x.Documents).Include(x => x.Interviews).ToListAsync();
        Assert.Equal(12, applications.Count);
        Assert.All(applications, x => Assert.Equal(x.Status, x.Events.Where(e => e.ToStatus is not null).OrderBy(e => e.OccurredAt).Last().ToStatus));
        Assert.All(applications.Where(x => x.Status != ApplicationStatus.Entwurf), x => Assert.NotNull(x.AppliedOn));
        Assert.Contains(applications, x => x.Deadline > new DateOnly(2026, 10, 23));
        Assert.Contains(applications.SelectMany(x => x.Interviews), i => i.Status == InterviewStatus.Geplant && i.ScheduledAt > new DateTime(2026, 10, 23));
        Assert.Contains(applications, x => x.LocationPrecision == LocationPrecision.Remote);

        var documents = applications.SelectMany(x => x.Documents).ToList();
        Assert.Equal(2, documents.Count);
        foreach (var document in documents)
        {
            await using var file = database.Storage.OpenRead(document.StoredFileName)!;
            Assert.Equal(document.Sha256, Convert.ToHexStringLower(await SHA256.HashDataAsync(file)));
        }
        Assert.True(DocumentService.ValidateUpload("x.pdf", DemoSeeder.SimplePdf(["Test"]), "Lebenslauf").Count == 0);

        await using var b = database.CreateContext(TestDatabase.UserB);
        Assert.Empty(await b.Applications.ToListAsync());
    }
}
