using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// A real SQLite database that lives in memory for the duration of one test,
// plus an empty temporary folder for document files.
// Two user accounts exist; contexts act as UserA unless another user is given.
// The connection must stay open – closing it deletes the database.
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _documentDirectory =
        Path.Combine(Path.GetTempPath(), "BewerbungsPilot.Tests", Guid.NewGuid().ToString("N"));

    public const string UserA = "user-a";
    public const string UserB = "user-b";

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.Migrate(); // uses the same migrations as the real app
        foreach (var id in new[] { UserA, UserB })
            db.Users.Add(new IdentityUser { Id = id, UserName = $"{id}@example.org", Email = $"{id}@example.org" });
        db.SaveChanges();
        Storage = new DocumentStorage(_documentDirectory);
    }

    public DocumentStorage Storage { get; }

    // Each call returns a fresh DbContext, like a new HTTP request would get.
    // userId null = no logged-in user (sees nothing, may change nothing).
    public AppDbContext CreateContext(string? userId = UserA) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options, new CurrentUser(userId));

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_documentDirectory))
            Directory.Delete(_documentDirectory, recursive: true);
    }
}

// A clock with a fixed time, so date-dependent rules can be tested reliably.
public class FixedClock : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
