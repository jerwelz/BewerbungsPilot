using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Deadline rules, calendar events and "Anstehende Termine".
// "Today" in all tests: Thursday, 22.10.2026, 09:00 local time.
public class ScheduleTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private static readonly DateOnly Today = new(2026, 10, 22);

    public void Dispose() => _database.Dispose();

    private async Task<int> CreateApplicationAsync(string company, ApplicationStatus status, DateOnly? deadline)
    {
        using var db = _database.CreateContext();
        var appliedOn = status == ApplicationStatus.Entwurf ? (DateOnly?)null : new DateOnly(2026, 10, 1);
        var input = new ApplicationInput(company, "Auszubildender FIAE", null, null, deadline, appliedOn, status, null);
        return (await new ApplicationService(db, _clock, _database.Storage).CreateAsync(input)).Id;
    }

    // Interviews are inserted directly, so past dates are possible for tests.
    private async Task AddInterviewAsync(int applicationId, DateTime at, InterviewStatus status = InterviewStatus.Geplant)
    {
        using var db = _database.CreateContext();
        db.Interviews.Add(new Interview { JobApplicationId = applicationId, ScheduledAt = at, Type = InterviewType.Video, Status = status });
        await db.SaveChangesAsync();
    }

    private ScheduleService Service(AppDbContext db) => new(db, _clock);

    // ---------- Deadline rules ----------

    [Theory]
    [InlineData(ApplicationStatus.Entwurf, "2026-10-21", ScheduleState.Overdue)]  // yesterday
    [InlineData(ApplicationStatus.Entwurf, "2026-10-22", ScheduleState.DueSoon)]  // today: still OK
    [InlineData(ApplicationStatus.Entwurf, "2026-10-29", ScheduleState.DueSoon)]  // in 7 days
    [InlineData(ApplicationStatus.Entwurf, "2026-10-30", ScheduleState.Open)]     // in 8 days
    [InlineData(ApplicationStatus.Beworben, "2026-10-01", ScheduleState.Done)]    // submitted → never overdue
    [InlineData(ApplicationStatus.Absage, "2026-10-01", ScheduleState.Done)]      // no longer relevant
    [InlineData(ApplicationStatus.InPruefung, "2026-12-01", ScheduleState.Done)]
    public void DeadlineState_FollowsTheRules(ApplicationStatus status, string deadline, ScheduleState expected)
    {
        Assert.Equal(expected, ScheduleRules.DeadlineState(status, DateOnly.Parse(deadline), Today));
    }

    [Fact]
    public void DeadlineState_NoDeadline_IsNull()
    {
        Assert.Null(ScheduleRules.DeadlineState(ApplicationStatus.Entwurf, null, Today));
    }

    [Theory]
    [InlineData(InterviewStatus.Geplant, "2026-10-21 16:00", ScheduleState.Overdue)]   // yesterday, no result recorded
    [InlineData(InterviewStatus.Geplant, "2026-10-22 08:00", ScheduleState.Open)]      // earlier today: still open
    [InlineData(InterviewStatus.Geplant, "2026-10-28 14:30", ScheduleState.Open)]
    [InlineData(InterviewStatus.Abgeschlossen, "2026-10-21 16:00", ScheduleState.Done)]
    [InlineData(InterviewStatus.Abgesagt, "2026-10-28 14:30", ScheduleState.Cancelled)]
    public void InterviewState_FollowsTheRules(InterviewStatus status, string at, ScheduleState expected)
    {
        Assert.Equal(expected, ScheduleRules.InterviewState(status, DateTime.Parse(at), Today));
    }

    // ---------- Deadline editing & history ----------

    private async Task SetDeadlineAsync(int id, DateOnly? deadline)
    {
        using var db = _database.CreateContext();
        var service = new ApplicationService(db, _clock, _database.Storage);
        var a = await service.GetByIdAsync(id);
        await service.UpdateAsync(id, new ApplicationInput(a!.CompanyName, a.Position, a.Location, a.JobUrl,
            deadline, a.AppliedOn, a.Status, a.Notes));
    }

    private async Task<List<string>> DeadlineEventsAsync(int id)
    {
        using var db = _database.CreateContext();
        return await db.ApplicationEvents
            .Where(e => e.JobApplicationId == id && e.Type == EventType.DeadlineChanged)
            .OrderBy(e => e.Id).Select(e => e.Description).ToListAsync();
    }

    [Fact]
    public async Task Deadline_SetChangeRemove_EachRecordedOnce()
    {
        var id = await CreateApplicationAsync("Nordlicht", ApplicationStatus.Entwurf, null);

        await SetDeadlineAsync(id, new DateOnly(2026, 11, 15));
        await SetDeadlineAsync(id, new DateOnly(2026, 11, 15)); // unchanged → no entry
        await SetDeadlineAsync(id, new DateOnly(2026, 11, 30));
        await SetDeadlineAsync(id, null);

        Assert.Equal(new[]
        {
            "Bewerbungsfrist festgelegt: 15.11.2026",
            "Bewerbungsfrist geändert: 15.11.2026 → 30.11.2026",
            "Bewerbungsfrist entfernt (war 30.11.2026)"
        }, await DeadlineEventsAsync(id));

        using var db = _database.CreateContext();
        Assert.Null((await db.Applications.SingleAsync()).Deadline);
    }

    // ---------- Calendar ----------

    [Fact]
    public async Task Calendar_ReturnsEventsInRange_AcrossMonthBoundary_InDateOrder()
    {
        var a = await CreateApplicationAsync("Alpha", ApplicationStatus.Entwurf, new DateOnly(2026, 11, 2));
        var b = await CreateApplicationAsync("Beta", ApplicationStatus.Beworben, null);
        await AddInterviewAsync(b, new DateTime(2026, 10, 30, 23, 30, 0));  // late evening, last days of October
        await AddInterviewAsync(b, new DateTime(2026, 11, 2, 9, 0, 0));    // same day as Alpha's deadline
        await AddInterviewAsync(b, new DateTime(2026, 11, 9, 0, 0, 0));    // just outside the range
        await AddInterviewAsync(b, new DateTime(2026, 10, 25, 23, 59, 0)); // just before the range

        using var db = _database.CreateContext();
        var events = await Service(db).GetEventsAsync(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 8));

        Assert.Equal(3, events.Count);
        Assert.Equal(new DateOnly(2026, 10, 30), events[0].Date);
        Assert.Equal(new TimeOnly(23, 30), events[0].Time);
        Assert.Equal(CalendarEventKind.Interview, events[1].Kind);  // 02.11. 09:00 …
        Assert.Equal(CalendarEventKind.Deadline, events[2].Kind);   // … before the all-day deadline
        Assert.Equal(a, events[2].ApplicationId);
        Assert.Null(events[2].Time);
    }

    [Fact]
    public async Task Calendar_ShowsDoneDeadlinesWithDoneState()
    {
        await CreateApplicationAsync("Gamma", ApplicationStatus.Beworben, new DateOnly(2026, 10, 10));

        using var db = _database.CreateContext();
        var events = await Service(db).GetEventsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(ScheduleState.Done, Assert.Single(events).State);
    }

    // ---------- Upcoming (dashboard) ----------

    [Fact]
    public async Task Upcoming_OnlyActionableEvents_InChronologicalOrder()
    {
        var draft = await CreateApplicationAsync("Entwurf GmbH", ApplicationStatus.Entwurf, new DateOnly(2026, 10, 25));
        var overdueDraft = await CreateApplicationAsync("Verpasst AG", ApplicationStatus.Entwurf, new DateOnly(2026, 10, 20));
        await CreateApplicationAsync("Versendet KG", ApplicationStatus.Beworben, new DateOnly(2026, 10, 24)); // submitted → excluded
        var interviewing = await CreateApplicationAsync("Gespräch GmbH", ApplicationStatus.Vorstellungsgespraech, null);
        await AddInterviewAsync(interviewing, new DateTime(2026, 10, 23, 10, 0, 0));
        await AddInterviewAsync(interviewing, new DateTime(2026, 10, 24, 10, 0, 0), InterviewStatus.Abgesagt);      // excluded
        await AddInterviewAsync(interviewing, new DateTime(2026, 10, 19, 10, 0, 0), InterviewStatus.Abgeschlossen); // excluded

        using var db = _database.CreateContext();
        var upcoming = await Service(db).GetUpcomingAsync();

        Assert.Equal(3, upcoming.Count);
        Assert.Equal((overdueDraft, ScheduleState.Overdue), (upcoming[0].ApplicationId, upcoming[0].State));
        Assert.Equal((interviewing, CalendarEventKind.Interview), (upcoming[1].ApplicationId, upcoming[1].Kind));
        Assert.Equal((draft, ScheduleState.DueSoon), (upcoming[2].ApplicationId, upcoming[2].State));
    }

    [Fact]
    public async Task Upcoming_IsLimited()
    {
        var id = await CreateApplicationAsync("Viele Termine", ApplicationStatus.Vorstellungsgespraech, null);
        for (var day = 1; day <= 12; day++)
            await AddInterviewAsync(id, new DateTime(2026, 11, day, 10, 0, 0));

        using var db = _database.CreateContext();
        var upcoming = await Service(db).GetUpcomingAsync();

        Assert.Equal(ScheduleService.UpcomingCount, upcoming.Count);
        Assert.Equal(new DateOnly(2026, 11, 1), upcoming[0].Date);
    }

    [Fact]
    public async Task Upcoming_EmptyDatabase_ReturnsEmptyList()
    {
        using var db = _database.CreateContext();

        Assert.Empty(await Service(db).GetUpcomingAsync());
    }
}
