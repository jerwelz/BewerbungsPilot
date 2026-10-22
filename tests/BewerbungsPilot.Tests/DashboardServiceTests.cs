using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Tests;

public class DashboardServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private async Task AddAsync(ApplicationStatus status, DateOnly? appliedOn = null)
    {
        using var db = _database.CreateContext();
        await new ApplicationService(db, _clock, _database.Storage).CreateAsync(
            new ApplicationInput("Firma", "Stelle", null, null, null, appliedOn, status, null));
    }

    private async Task<DashboardResponse> GetDashboardAsync()
    {
        using var db = _database.CreateContext();
        return await new DashboardService(db, _clock).GetDashboardAsync();
    }

    [Fact]
    public async Task EmptyDatabase_ReturnsZerosAndNoPercentages()
    {
        var dashboard = await GetDashboardAsync();

        Assert.Equal(0, dashboard.Total);
        Assert.Null(dashboard.ResponseRatePercent);
        Assert.All(dashboard.StatusDistribution, s => Assert.Null(s.Percent));
        Assert.Equal(DashboardService.MonthsShown, dashboard.MonthlyActivity.Count);
        Assert.All(dashboard.MonthlyActivity, m => Assert.Equal(0, m.Count));
        Assert.Empty(dashboard.RecentActivity);
    }

    [Fact]
    public async Task CountsApplicationsPerStatusGroup()
    {
        await AddAsync(ApplicationStatus.Entwurf);
        await AddAsync(ApplicationStatus.Beworben);
        await AddAsync(ApplicationStatus.InPruefung);
        await AddAsync(ApplicationStatus.Vorstellungsgespraech);
        await AddAsync(ApplicationStatus.Angebot);
        await AddAsync(ApplicationStatus.Absage);
        await AddAsync(ApplicationStatus.Absage);

        var dashboard = await GetDashboardAsync();

        Assert.Equal(7, dashboard.Total);
        Assert.Equal(1, dashboard.Drafts);
        Assert.Equal(2, dashboard.InProgress); // Beworben + In Prüfung
        Assert.Equal(1, dashboard.Interviews);
        Assert.Equal(1, dashboard.Offers);
        Assert.Equal(2, dashboard.Rejections);
        // 6 submitted, 4 with a response (interview, offer, 2 rejections) → 67 %
        Assert.Equal(67, dashboard.ResponseRatePercent);
        Assert.Equal(29, dashboard.StatusDistribution.Single(s => s.Status == ApplicationStatus.Absage).Percent); // 2 of 7
    }

    [Fact]
    public async Task MonthlyActivity_CountsByApplicationDate_IncludingEmptyMonths()
    {
        await AddAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 3));
        await AddAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 20));
        await AddAsync(ApplicationStatus.Absage, new DateOnly(2026, 7, 15));
        await AddAsync(ApplicationStatus.Beworben, new DateOnly(2025, 12, 1)); // older than 6 months
        await AddAsync(ApplicationStatus.Entwurf); // never submitted

        var months = (await GetDashboardAsync()).MonthlyActivity;

        Assert.Equal((2026, 5), (months[0].Year, months[0].Month)); // May … October
        Assert.Equal((2026, 10), (months[^1].Year, months[^1].Month));
        Assert.Equal(new[] { 0, 0, 1, 0, 0, 2 }, months.Select(m => m.Count));
    }

    [Fact]
    public async Task RecentActivity_NewestFirst()
    {
        await AddAsync(ApplicationStatus.Entwurf);
        await AddAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 1));

        var activity = (await GetDashboardAsync()).RecentActivity;

        Assert.Equal(2, activity.Count);
        Assert.True(activity[0].ApplicationId > activity[1].ApplicationId);
    }
}
