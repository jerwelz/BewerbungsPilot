namespace BewerbungsPilot.Logic;

// Demo mode only (registered in Program.cs): deletes expired demo accounts at startup and then
// every Demo:CleanupInterval (default 10 minutes). Without it, expired accounts would only be
// removed when the next visitor clicks "Demo starten".
public class DemoCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DemoCleanupService> _logger;
    private readonly TimeSpan _interval;

    public DemoCleanupService(IServiceScopeFactory scopes, ILogger<DemoCleanupService> logger, IConfiguration configuration)
    {
        _scopes = scopes;
        _logger = logger;
        _interval = configuration.GetValue<TimeSpan?>("Demo:CleanupInterval") ?? TimeSpan.FromMinutes(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            await RunOnceAsync();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<int> RunOnceAsync()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var deleted = await scope.ServiceProvider.GetRequiredService<DemoAccounts>().DeleteExpiredAsync();
            if (deleted > 0)
                _logger.LogInformation("{Count} abgelaufene Demo-Konten gelöscht.", deleted);
            return deleted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Try again at the next interval; the app keeps running.
            _logger.LogError(exception, "Abgelaufene Demo-Konten konnten nicht gelöscht werden.");
            return 0;
        }
    }
}
