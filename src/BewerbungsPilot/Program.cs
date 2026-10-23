using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BewerbungsPilot;
using BewerbungsPilot.Data;
using BewerbungsPilot.Endpoints;
using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

Console.OutputEncoding = System.Text.Encoding.UTF8; // German messages in the Windows console
var builder = WebApplication.CreateBuilder(args);

// Operator commands ("migrate", "claim-legacy-data", "reset-password") must be the first argument.
var command = args.Length > 0 && AdminCommands.Names.Contains(args[0]) ? args[0] : null;

// --- Database: one SQLite file inside the (git-ignored) storage directory ---
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:DataDirectory"] ?? "storage");

// Demo mode (fictional data for visitors, see DemoAccounts.cs) never runs on the default data
// folder – that is where the real data lives. Checked before anything in it is opened.
var demoMode = DemoAccounts.IsEnabled(builder.Configuration);
if (demoMode && DemoAccounts.IsDefaultDataDirectory(builder.Environment.ContentRootPath, dataDirectory))
{
    Console.Error.WriteLine("Der Demo-Modus braucht ein eigenes Datenverzeichnis (z. B. --Storage:DataDirectory storage-demo). " +
        "Die Anwendung wird nicht gestartet.");
    return 1;
}
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "bewerbungspilot.db");

// The logged-in user of the current request; the DbContext filters all data by it.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(services => CurrentUser.FromHttpContext(services.GetRequiredService<IHttpContextAccessor>()));
// Scoped (one context per request) – never use DbContext pooling: the context holds the user.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

// --- Documents: private folder next to the database (NOT inside wwwroot) ---
builder.Services.AddSingleton(new DocumentStorage(Path.Combine(dataDirectory, "documents")));

// --- User accounts: ASP.NET Core Identity with a login cookie ---
builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        // Length instead of composition rules (NIST SP 800-63B).
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders(); // password reset token for the "reset-password" command

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    var development = builder.Environment.IsDevelopment();
    // "__Host-" makes the browser enforce HTTPS, Path=/ and no Domain – only possible over HTTPS.
    options.Cookie.Name = development ? "BewerbungsPilot.Auth" : "__Host-BewerbungsPilot";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = builder.Configuration.GetValue<TimeSpan?>("Auth:SessionLifetime") ?? TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    // This is an API: answer 401/403 instead of redirecting to a login page.
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
// Check the security stamp on every request: a password change or reset ends other sessions immediately.
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);

// Every endpoint requires a login unless it is explicitly marked AllowAnonymous (AuthEndpoints).
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = builder.Environment.IsDevelopment() ? "BewerbungsPilot.Antiforgery" : "__Host-BewerbungsPilot.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

// Login, registration and password change: limited attempts per client IP address.
builder.Services.AddRateLimiter(options =>
{
    var perMinute = builder.Configuration.GetValue<int?>("Auth:RequestsPerMinute") ?? 10;
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = (context, cancellationToken) => new ValueTask(Results.Problem(
        title: "Zu viele Versuche. Bitte warten Sie eine Minute.",
        statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext));
});

// Keys that sign the login and antiforgery cookies. Persisted, so a restart does not log
// everyone out. On Windows encrypted for the current Windows user (DPAPI).
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("BewerbungsPilot")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();

// Behind a reverse proxy (production): trust X-Forwarded-* ONLY from the configured proxy addresses.
var knownProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in knownProxies)
        options.KnownProxies.Add(IPAddress.Parse(proxy));
});

// --- Services (business logic) ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApplicationService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DocumentService>();
builder.Services.AddScoped<InterviewService>();
builder.Services.AddScoped<ScheduleService>();
builder.Services.AddScoped<TimelineService>();
builder.Services.AddScoped<WaitingQueueService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<MapService>();
builder.Services.AddScoped<DemoAccounts>();

// --- Place search (Nominatim, only on user request; see NominatimGeocoder.cs) ---
builder.Services.AddHttpClient<NominatimGeocoder>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Geocoding:BaseUrl"] ?? "https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        builder.Configuration["Geocoding:UserAgent"] ?? "BewerbungsPilot/1.0 (local job application manager)");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Send enums as text ("Beworben") instead of numbers in JSON.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (command is not null)
    return await AdminCommands.RunAsync(app, command, dataDirectory);

// Database schema. A NEW database is created automatically. An EXISTING database is only
// migrated by the explicit "migrate" command (backup + checks, see MigrationSafety.cs).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    switch (MigrationSafety.GetState(db))
    {
        case MigrationSafety.State.NewDatabase:
            db.Database.Migrate();
            break;
        case MigrationSafety.State.MigrationRequired:
            app.Logger.LogCritical(
                "Die Datenbank {Path} muss migriert werden. Die Anwendung wird nicht gestartet. " +
                "Bitte ausführen: dotnet run --project src/BewerbungsPilot -- migrate", databasePath);
            return 1;
    }

    // Second demo safeguard: a demo database contains demo accounts only.
    if (demoMode && !await DemoAccounts.ContainsOnlyDemoAccountsAsync(db))
    {
        app.Logger.LogCritical(
            "Die Datenbank {Path} enthält echte Benutzerkonten. Der Demo-Modus wird darauf nicht gestartet.", databasePath);
        return 1;
    }

    var unclaimed = await LegacyDataClaim.CountUnclaimedAsync(db);
    if (unclaimed > 0)
        app.Logger.LogWarning(
            "{Count} Bewerbungen aus der Zeit vor der Anmeldung sind noch keinem Konto zugeordnet und für niemanden sichtbar. " +
            "Zuordnen mit: dotnet run --project src/BewerbungsPilot -- claim-legacy-data --email <Konto>", unclaimed);
}

if (knownProxies.Length > 0)
    app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Security headers for every response; private API answers are never stored by the browser
// (also not in the back/forward cache after logout).
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "SAMEORIGIN"; // the PDF preview is a same-origin iframe
        headers["Referrer-Policy"] = "same-origin";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
            headers.Pragma = "no-cache";
        }
        return Task.CompletedTask;
    });
    await next();
});

// --- Frontend: static files from wwwroot (no private data inside; pages load data via the API) ---
app.UseDefaultFiles(); // "/" serves index.html (the dashboard)
app.UseStaticFiles(new StaticFileOptions
{
    // HTML pages are not cached, so "Back" after logout cannot show an old page.
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            context.Context.Response.Headers.CacheControl = "no-store";
    }
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// --- API ---
app.MapAuthEndpoints();
if (demoMode)
    app.MapDemoEndpoints(); // not mapped at all outside demo mode

// All application endpoints: login required, CSRF check, foreign data → 404 (ApiFilters.cs).
var api = app.MapGroup("").RequireAuthorization().AddEndpointFilter<ApiProtectionFilter>();
api.MapApplicationEndpoints();
api.MapDashboardEndpoints();
api.MapDocumentEndpoints();
api.MapInterviewEndpoints();
api.MapTimelineEndpoints();
api.MapAnalysisEndpoints();
api.MapMapEndpoints();

app.Run();
return 0;

// Lets the integration tests start the application (WebApplicationFactory<Program>).
public partial class Program;
