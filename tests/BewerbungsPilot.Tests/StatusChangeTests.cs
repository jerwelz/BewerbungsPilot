using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Status changes from the Kanban board (ChangeStatusAsync).
public class StatusChangeTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private async Task<int> CreateAsync(ApplicationStatus status, DateOnly? appliedOn = null)
    {
        using var db = _database.CreateContext();
        var input = new ApplicationInput("Nordlicht Software GmbH", "Auszubildender FIAE", "Hamburg",
            "https://example.org/stelle", new DateOnly(2026, 11, 30), appliedOn, status, "Wichtige Notiz");
        return (await new ApplicationService(db, _clock).CreateAsync(input)).Id;
    }

    private async Task ChangeAsync(int id, ApplicationStatus status)
    {
        using var db = _database.CreateContext();
        await new ApplicationService(db, _clock).ChangeStatusAsync(id, status);
    }

    private async Task<JobApplication> LoadAsync(int id)
    {
        using var db = _database.CreateContext();
        return await db.Applications.Include(a => a.Events).SingleAsync(a => a.Id == id);
    }

    [Fact]
    public async Task ChangeStatus_PersistsStatusWithExactlyOneHistoryEntry()
    {
        var id = await CreateAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 1));

        await ChangeAsync(id, ApplicationStatus.InPruefung);

        var saved = await LoadAsync(id);
        Assert.Equal(ApplicationStatus.InPruefung, saved.Status);
        var change = Assert.Single(saved.Events, e => e.Type == EventType.StatusChanged);
        Assert.Equal(ApplicationStatus.Beworben, change.FromStatus);
        Assert.Equal(ApplicationStatus.InPruefung, change.ToStatus);
    }

    [Fact]
    public async Task ChangeStatus_KeepsAllOtherFields()
    {
        var id = await CreateAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 1));
        var before = await LoadAsync(id);

        await ChangeAsync(id, ApplicationStatus.Vorstellungsgespraech);

        var after = await LoadAsync(id);
        Assert.Equal(before.CompanyName, after.CompanyName);
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.Location, after.Location);
        Assert.Equal(before.JobUrl, after.JobUrl);
        Assert.Equal(before.Deadline, after.Deadline);
        Assert.Equal(before.AppliedOn, after.AppliedOn);
        Assert.Equal(before.Notes, after.Notes);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task ChangeStatus_SameStatus_AddsNoHistoryEntry()
    {
        var id = await CreateAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 1));

        await ChangeAsync(id, ApplicationStatus.Beworben);

        Assert.Single((await LoadAsync(id)).Events); // only "created"
    }

    [Fact]
    public async Task ChangeStatus_SeveralMoves_OneEntryPerMove()
    {
        var id = await CreateAsync(ApplicationStatus.Entwurf);

        await ChangeAsync(id, ApplicationStatus.Beworben);
        await ChangeAsync(id, ApplicationStatus.InPruefung);
        await ChangeAsync(id, ApplicationStatus.Absage);

        var saved = await LoadAsync(id);
        Assert.Equal(3, saved.Events.Count(e => e.Type == EventType.StatusChanged));
        Assert.Equal(ApplicationStatus.Absage, saved.Status);
    }

    [Fact]
    public async Task ChangeStatus_FromDraft_SetsAppliedOnToToday()
    {
        var id = await CreateAsync(ApplicationStatus.Entwurf);

        await ChangeAsync(id, ApplicationStatus.Beworben);

        Assert.Equal(new DateOnly(2026, 10, 22), (await LoadAsync(id)).AppliedOn);
    }

    [Fact]
    public async Task ChangeStatus_NonexistentId_ReturnsNull()
    {
        using var db = _database.CreateContext();

        Assert.Null(await new ApplicationService(db, _clock).ChangeStatusAsync(999, ApplicationStatus.Angebot));
    }

    [Fact]
    public async Task ChangeStatus_IsReflectedInDashboard()
    {
        var id = await CreateAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 1));
        await CreateAsync(ApplicationStatus.Beworben, new DateOnly(2026, 10, 2));

        await ChangeAsync(id, ApplicationStatus.Angebot);

        using var db = _database.CreateContext();
        var dashboard = await new DashboardService(db, _clock).GetDashboardAsync();
        Assert.Equal(1, dashboard.InProgress);
        Assert.Equal(1, dashboard.Offers);
        Assert.Equal(50, dashboard.ResponseRatePercent);
    }
}
