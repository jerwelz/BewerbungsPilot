using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BewerbungsPilot.Data;

// GET /health (Program.cs): the hosting platform restarts the app when this fails.
// Only checks that the database file can be opened – it reads no user data.
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly AppDbContext _db;

    public DatabaseHealthCheck(AppDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Die Datenbank ist nicht erreichbar.");
}
