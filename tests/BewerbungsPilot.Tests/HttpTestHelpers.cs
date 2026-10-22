using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace BewerbungsPilot.Tests;

internal static class TestAssemblySetup
{
    // Safety net: if a test host ever started without its own data directory, it would use this
    // throwaway folder – never the real storage/ folder next to the project.
    [ModuleInitializer]
    internal static void PointDefaultDataDirectoryAway() =>
        Environment.SetEnvironmentVariable("Storage__DataDirectory",
            Path.Combine(Path.GetTempPath(), "BewerbungsPilot.Tests", "unconfigured-host"));
}

// The real application (Program.cs) in memory, with its own temporary data directory.
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public AppFactory(Dictionary<string, string?>? settings = null, TimeProvider? clock = null)
    {
        _settings = settings ?? [];
        Clock = clock;
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "BewerbungsPilot.Tests", "http-" + Guid.NewGuid().ToString("N"));
    public TimeProvider? Clock { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Storage:DataDirectory", DataDirectory);
        builder.UseSetting("Auth:AllowRegistration", "true");
        builder.UseSetting("Auth:RequestsPerMinute", "1000");
        builder.UseSetting("Geocoding:BaseUrl", "http://127.0.0.1:9/"); // never the real Nominatim
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        if (Clock is not null)
            builder.ConfigureTestServices(services => services.AddSingleton(Clock));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// A clock the tests can move forward (session expiration).
public class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}

// One browser: its own cookie jar (handled here, so tests can inspect and replay cookies)
// and its own antiforgery token.
public sealed class ApiSession
{
    public const string Password = "ein-sehr-sicheres-passwort";
    private readonly HttpClient _client;

    public ApiSession(AppFactory factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });
    }

    public Dictionary<string, string> Cookies { get; } = [];
    public string? Token { get; set; }
    public string? UserId { get; private set; }
    public List<string> LastSetCookies { get; } = [];

    public static async Task<ApiSession> RegisterAsync(AppFactory factory, string email, string password = Password)
    {
        var session = new ApiSession(factory);
        await session.RefreshTokenAsync();
        var response = await session.SendAsync(HttpMethod.Post, "/api/auth/register", new { email, password });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        session.UserId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await session.RefreshTokenAsync(); // the token is bound to the user
        return session;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password = Password, bool rememberMe = false)
    {
        await RefreshTokenAsync();
        var response = await SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password, rememberMe });
        if (response.IsSuccessStatusCode)
        {
            UserId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            await RefreshTokenAsync();
        }
        return response;
    }

    public async Task RefreshTokenAsync()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/auth/antiforgery");
        Token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
    }

    // csrfToken: null → send the session's token; "" → send no token; anything else → send that.
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? csrfToken = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content ?? (body is null ? null : JsonContent.Create(body)) };
        if (Cookies.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        var token = csrfToken ?? Token;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("X-CSRF-TOKEN", token);

        var response = await _client.SendAsync(request);
        LastSetCookies.Clear();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in SetCookieHeaderValue.ParseList(setCookies.ToList()))
            {
                LastSetCookies.Add(header.ToString());
                if (header.Expires < DateTimeOffset.UtcNow || header.Value.Length == 0)
                    Cookies.Remove(header.Name.ToString());
                else
                    Cookies[header.Name.ToString()] = header.Value.ToString();
            }
        }
        return response;
    }

    public async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await SendAsync(HttpMethod.Get, url);
        Assert.True(response.IsSuccessStatusCode, $"GET {url} → {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
