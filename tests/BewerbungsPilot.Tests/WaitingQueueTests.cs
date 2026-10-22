using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Tests;

// "Wartende Bewerbungen". Today: 22.10.2026.
public class WaitingQueueTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static FixedClock ClockAt(int month, int day) => new(new DateTimeOffset(2026, month, day, 9, 0, 0, TimeSpan.Zero));

    private async Task<int> CreateAsync(string company, ApplicationStatus status, DateOnly? appliedOn, FixedClock clock)
    {
        using var db = _database.CreateContext();
        return (await new ApplicationService(db, clock, _database.Storage).CreateAsync(
            new ApplicationInput(company, "Azubi FIAE", null, null, null, appliedOn, status, null))).Id;
    }

    private async Task<List<WaitingItem>> QueueAsync(WaitingSort sort)
    {
        using var db = _database.CreateContext();
        return await new WaitingQueueService(db, ClockAt(10, 22)).GetQueueAsync(sort);
    }

    [Fact]
    public async Task OnlyWaitingStatuses_AreListed()
    {
        var early = ClockAt(10, 1);
        await CreateAsync("Entwurf", ApplicationStatus.Entwurf, null, early);
        await CreateAsync("Beworben", ApplicationStatus.Beworben, new DateOnly(2026, 10, 1), early);
        await CreateAsync("Prüfung", ApplicationStatus.InPruefung, new DateOnly(2026, 10, 1), early);
        await CreateAsync("Gespräch", ApplicationStatus.Vorstellungsgespraech, new DateOnly(2026, 10, 1), early);
        await CreateAsync("Angebot", ApplicationStatus.Angebot, new DateOnly(2026, 10, 1), early);
        await CreateAsync("Absage", ApplicationStatus.Absage, new DateOnly(2026, 10, 1), early);

        var queue = await QueueAsync(WaitingSort.SinceSubmission);

        Assert.Equal(new[] { "Beworben", "Gespräch", "Prüfung" }, queue.Select(q => q.CompanyName).Order());
    }

    [Fact]
    public async Task BothMeasurements_AreCalculatedSeparately()
    {
        var id = await CreateAsync("Nordlicht", ApplicationStatus.Beworben, new DateOnly(2026, 9, 22), ClockAt(9, 25));
        using (var db = _database.CreateContext())
        {
            await new ApplicationService(db, ClockAt(10, 12), _database.Storage).ChangeStatusAsync(id, ApplicationStatus.InPruefung);
        }

        var item = Assert.Single(await QueueAsync(WaitingSort.SinceSubmission));

        Assert.Equal(30, item.DaysSinceSubmission);   // 22.09. → 22.10.
        Assert.Equal(10, item.DaysSinceStatusChange); // recorded 12.10. → 22.10.
    }

    [Fact]
    public async Task NoteEdit_DoesNotResetTheStatusChangeClock()
    {
        var id = await CreateAsync("Nordlicht", ApplicationStatus.Beworben, new DateOnly(2026, 10, 1), ClockAt(10, 2));
        using (var db = _database.CreateContext())
        {
            var service = new ApplicationService(db, ClockAt(10, 20), _database.Storage);
            await service.UpdateAsync(id, new ApplicationInput("Nordlicht", "Azubi FIAE", null, null, null,
                new DateOnly(2026, 10, 1), ApplicationStatus.Beworben, "Neue Notiz"));
        }

        Assert.Equal(20, Assert.Single(await QueueAsync(WaitingSort.SinceStatusChange)).DaysSinceStatusChange);
    }

    [Fact]
    public async Task Sorting_LongestFirst_MissingValuesLast()
    {
        await CreateAsync("Kurz", ApplicationStatus.Beworben, new DateOnly(2026, 10, 15), ClockAt(10, 15));
        await CreateAsync("Lang", ApplicationStatus.Beworben, new DateOnly(2026, 9, 1), ClockAt(10, 18));
        using (var db = _database.CreateContext())
        {
            // Old data without any status entry and without submission date
            db.Applications.Add(new JobApplication { CompanyName = "Ohne Daten", Position = "Azubi", Status = ApplicationStatus.InPruefung,
                CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), StatusChangedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }

        var bySubmission = await QueueAsync(WaitingSort.SinceSubmission);
        var byChange = await QueueAsync(WaitingSort.SinceStatusChange);

        Assert.Equal(new[] { "Lang", "Kurz", "Ohne Daten" }, bySubmission.Select(q => q.CompanyName));
        Assert.Equal(new[] { "Kurz", "Lang", "Ohne Daten" }, byChange.Select(q => q.CompanyName)); // recorded 15.10. vs 18.10.
        var unknown = bySubmission[^1];
        Assert.Null(unknown.DaysSinceSubmission);
        Assert.Null(unknown.DaysSinceStatusChange);
        Assert.Null(unknown.LastStatusChangeAt);
    }

    [Fact]
    public async Task EmptyDatabase_GivesEmptyQueue()
    {
        Assert.Empty(await QueueAsync(WaitingSort.SinceSubmission));
    }
}
