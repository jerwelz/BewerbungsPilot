using System.Globalization;
using BewerbungsPilot.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Demo mode ("Demo:Enabled"): every visitor gets a throwaway account with fictional data.
// The accounts are isolated from each other exactly like real accounts (query filters +
// ownership guard), so visitors never see or change each other's data.
//
// A demo account is marked by the user claim DemoClaimType (value: creation time, UTC).
// It has NO password: nobody can log in to it, the "Demo starten" button signs the visitor in.
// Expired demo accounts are deleted when the next visitor starts a demo – no background job.
public class DemoAccounts
{
    public const string DemoClaimType = "bewerbungspilot:demo-created-at";

    private readonly AppDbContext _db; // the request's context – used only for the Identity tables
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly UserManager<IdentityUser> _users;
    private readonly DocumentStorage _storage;
    private readonly TimeProvider _clock;
    private readonly IConfiguration _configuration;

    public DemoAccounts(AppDbContext db, DbContextOptions<AppDbContext> options, UserManager<IdentityUser> users,
        DocumentStorage storage, TimeProvider clock, IConfiguration configuration)
    {
        _db = db;
        _options = options;
        _users = users;
        _storage = storage;
        _clock = clock;
        _configuration = configuration;
    }

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue<bool>("Demo:Enabled");

    private TimeSpan Lifetime => _configuration.GetValue<TimeSpan?>("Demo:AccountLifetime") ?? TimeSpan.FromHours(4);
    private int MaxAccounts => _configuration.GetValue<int?>("Demo:MaxAccounts") ?? 200;

    // Startup check: demo mode must never use the default data folder <content root>/storage.
    public static bool IsDefaultDataDirectory(string contentRoot, string dataDirectory) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(contentRoot, dataDirectory))),
            Path.GetFullPath(Path.Combine(contentRoot, "storage")), StringComparison.OrdinalIgnoreCase);

    // Startup check: a demo database may contain demo accounts only. A real account means the
    // demo was started on real data by mistake – the app must not start.
    public static async Task<bool> ContainsOnlyDemoAccountsAsync(AppDbContext db)
    {
        var demoUsers = await db.UserClaims.Where(c => c.ClaimType == DemoClaimType).Select(c => c.UserId).Distinct().CountAsync();
        return await db.Users.CountAsync() == demoUsers;
    }

    public static async Task<bool> IsDemoUserAsync(UserManager<IdentityUser> users, IdentityUser user) =>
        (await users.GetClaimsAsync(user)).Any(c => c.Type == DemoClaimType);

    // Creates a new demo account with fictional data. Returns null when too many demo
    // accounts are active (the caller answers 429).
    public async Task<IdentityUser?> CreateAsync()
    {
        await DeleteExpiredAsync();
        if (await _db.UserClaims.CountAsync(c => c.ClaimType == DemoClaimType) >= MaxAccounts)
            return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        var email = $"demo-{Guid.NewGuid():N}"[..17] + "@demo.invalid"; // .invalid: can never be a real address
        var user = new IdentityUser { UserName = email, Email = email };

        // User and demo marker in one transaction: there must never be an unmarked account.
        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            ThrowIfFailed(await _users.CreateAsync(user)); // no password: login only via "Demo starten"
            ThrowIfFailed(await _users.AddClaimAsync(user, new(DemoClaimType, now.ToString("O", CultureInfo.InvariantCulture))));
            await transaction.CommitAsync();
        }

        // The data is written as the new user, so the ownership guard applies as for any request.
        await using var userDb = new AppDbContext(_options, new CurrentUser(user.Id));
        await new DemoSeeder(userDb, _storage, _clock).SeedAsync();
        return user;
    }

    // Deletes demo accounts older than the lifetime, with all their data and files.
    // Only accounts carrying the demo claim are ever considered.
    public async Task<int> DeleteExpiredAsync()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - Lifetime;
        var markers = await _db.UserClaims.Where(c => c.ClaimType == DemoClaimType).ToListAsync();
        var expired = markers
            .Where(c => DateTime.TryParse(c.ClaimValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var created) && created < cutoff)
            .Select(c => c.UserId)
            .Distinct()
            .ToList();

        foreach (var userId in expired)
        {
            await using (var userDb = new AppDbContext(_options, new CurrentUser(userId)))
            {
                var applications = await userDb.Applications.Include(a => a.Documents).ToListAsync();
                var files = applications.SelectMany(a => a.Documents).Select(d => d.StoredFileName).ToList();
                userDb.Applications.RemoveRange(applications); // history, documents, interviews: cascade
                await userDb.SaveChangesAsync();
                foreach (var file in files)
                    _storage.TryDelete(file);
            }

            var user = await _users.FindByIdAsync(userId);
            if (user is not null)
                ThrowIfFailed(await _users.DeleteAsync(user));
        }
        return expired.Count;
    }

    private static void ThrowIfFailed(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException("Demo-Konto: " + string.Join(", ", result.Errors.Select(e => e.Code)));
    }
}
