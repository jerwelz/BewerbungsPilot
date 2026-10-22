using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// "Now" in all tests: Thursday, 22.10.2026, 09:00 local time.
public class InterviewTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private static readonly DateTime NextWeek = new(2026, 10, 28, 14, 30, 0);

    private static InterviewInput Input(DateTime? at = null, InterviewStatus status = InterviewStatus.Geplant,
        InterviewType type = InterviewType.Video, string? notes = null) =>
        new(at ?? NextWeek, type, "https://meet.example.org/abc", "Frau Berg", notes, status);

    private async Task<int> CreateApplicationAsync(ApplicationStatus status = ApplicationStatus.Beworben)
    {
        using var db = _database.CreateContext();
        var input = new ApplicationInput("Nordlicht Software GmbH", "Auszubildender FIAE", null, null, null,
            new DateOnly(2026, 10, 1), status, null);
        return (await new ApplicationService(db, _clock, _database.Storage).CreateAsync(input)).Id;
    }

    private InterviewService Service(AppDbContext db) => new(db, _clock);

    private async Task<Interview?> CreateAsync(int applicationId, InterviewInput input)
    {
        using var db = _database.CreateContext();
        return await Service(db).CreateAsync(applicationId, input);
    }

    private async Task UpdateAsync(int interviewId, InterviewInput input)
    {
        using var db = _database.CreateContext();
        await Service(db).UpdateAsync(interviewId, input);
    }

    private async Task<List<ApplicationEvent>> EventsAsync(int applicationId, EventType? type = null)
    {
        using var db = _database.CreateContext();
        return await db.ApplicationEvents
            .Where(e => e.JobApplicationId == applicationId && (type == null || e.Type == type))
            .ToListAsync();
    }

    // ---------- Create ----------

    [Fact]
    public async Task Create_SavesInterviewWithHistory_AndKeepsApplicationStatus()
    {
        var applicationId = await CreateApplicationAsync(ApplicationStatus.Beworben);

        var created = await CreateAsync(applicationId, Input());

        using var db = _database.CreateContext();
        var saved = await db.Interviews.SingleAsync();
        Assert.Equal(created!.Id, saved.Id);
        Assert.Equal(NextWeek, saved.ScheduledAt);
        Assert.Equal(DateTimeKind.Unspecified, saved.ScheduledAt.Kind); // local wall-clock time, not UTC
        Assert.Equal(InterviewType.Video, saved.Type);
        Assert.Equal("Frau Berg", saved.ContactPerson);
        Assert.Equal(ApplicationStatus.Beworben, (await db.Applications.SingleAsync()).Status);

        var entry = Assert.Single(await EventsAsync(applicationId, EventType.InterviewScheduled));
        Assert.Equal("Vorstellungsgespräch (Video) am 28.10.2026, 14:30 Uhr geplant", entry.Description);
    }

    [Fact]
    public async Task Create_SeveralInterviewsForOneApplication()
    {
        var applicationId = await CreateApplicationAsync();

        await CreateAsync(applicationId, Input(new DateTime(2026, 10, 28, 9, 0, 0), type: InterviewType.Telefon));
        await CreateAsync(applicationId, Input(new DateTime(2026, 11, 4, 13, 0, 0), type: InterviewType.VorOrt));

        using var db = _database.CreateContext();
        var application = await new ApplicationService(db, _clock, _database.Storage).GetByIdAsync(applicationId);
        Assert.Equal(2, application!.Interviews.Count);
        Assert.True(application.Interviews[0].ScheduledAt < application.Interviews[1].ScheduledAt); // date order
    }

    [Fact]
    public async Task Create_MissingApplication_ReturnsNull()
    {
        Assert.Null(await CreateAsync(999, Input()));
    }

    [Fact]
    public async Task Create_DropsSeconds()
    {
        var applicationId = await CreateApplicationAsync();

        var created = await CreateAsync(applicationId, Input(new DateTime(2026, 10, 28, 14, 30, 45)));

        Assert.Equal(new DateTime(2026, 10, 28, 14, 30, 0), created!.ScheduledAt);
    }

    // ---------- Validation ----------

    [Fact]
    public void Validate_MissingDate_IsRejected()
    {
        using var db = _database.CreateContext();

        var errors = Service(db).Validate(Input() with { ScheduledAt = null });

        Assert.Equal("Bitte geben Sie Datum und Uhrzeit an.", Assert.Single(errors["scheduledAt"]));
    }

    [Fact]
    public void Validate_UnrealisticYear_IsRejected()
    {
        using var db = _database.CreateContext();

        Assert.True(Service(db).Validate(Input(new DateTime(1900, 1, 1, 10, 0, 0))).ContainsKey("scheduledAt"));
    }

    [Fact]
    public void Validate_PlannedInterviewInThePast_IsRejected()
    {
        using var db = _database.CreateContext();

        var errors = Service(db).Validate(Input(new DateTime(2026, 10, 21, 10, 0, 0)));

        Assert.Contains("Vergangenheit", Assert.Single(errors["scheduledAt"]));
    }

    [Fact]
    public void Validate_CompletedInterviewInThePast_IsAllowed()
    {
        using var db = _database.CreateContext();

        Assert.Empty(Service(db).Validate(Input(new DateTime(2026, 10, 21, 10, 0, 0), InterviewStatus.Abgeschlossen)));
    }

    [Fact]
    public void Validate_EditingOverdueInterviewWithoutMovingIt_IsAllowed()
    {
        using var db = _database.CreateContext();
        var past = new DateTime(2026, 10, 20, 10, 0, 0);
        var existing = new Interview { ScheduledAt = past, Status = InterviewStatus.Geplant };

        Assert.Empty(Service(db).Validate(Input(past, notes: "Nur Notiz geändert"), existing));
    }

    // ---------- Update & history ----------

    [Fact]
    public async Task Update_NewTime_AddsRescheduledEntry()
    {
        var applicationId = await CreateApplicationAsync();
        var interview = await CreateAsync(applicationId, Input());

        await UpdateAsync(interview!.Id, Input(new DateTime(2026, 10, 30, 10, 0, 0)));

        var entry = Assert.Single(await EventsAsync(applicationId, EventType.InterviewRescheduled));
        Assert.Equal("Vorstellungsgespräch verschoben: 28.10.2026, 14:30 Uhr → 30.10.2026, 10:00 Uhr", entry.Description);
    }

    [Theory]
    [InlineData(InterviewStatus.Abgeschlossen, EventType.InterviewCompleted, "Vorstellungsgespräch abgeschlossen")]
    [InlineData(InterviewStatus.Abgesagt, EventType.InterviewCancelled, "Vorstellungsgespräch abgesagt")]
    public async Task Update_StatusChange_AddsMatchingEntry(InterviewStatus status, EventType expectedType, string expectedText)
    {
        var applicationId = await CreateApplicationAsync();
        var interview = await CreateAsync(applicationId, Input());

        await UpdateAsync(interview!.Id, Input(status: status));

        var entry = Assert.Single(await EventsAsync(applicationId, expectedType));
        Assert.StartsWith(expectedText, entry.Description);
        using var db = _database.CreateContext();
        Assert.Equal(status, (await db.Interviews.SingleAsync()).Status);
    }

    [Fact]
    public async Task Update_OnlyNotes_AddsNoHistoryEntry()
    {
        var applicationId = await CreateApplicationAsync();
        var interview = await CreateAsync(applicationId, Input());
        var before = (await EventsAsync(applicationId)).Count;

        await UpdateAsync(interview!.Id, Input(notes: "Fragen zu Projekten vorbereiten"));

        Assert.Equal(before, (await EventsAsync(applicationId)).Count);
        using var db = _database.CreateContext();
        Assert.Equal("Fragen zu Projekten vorbereiten", (await db.Interviews.SingleAsync()).Notes);
    }

    [Fact]
    public async Task Update_TimeAndStatus_AddsTwoEntries()
    {
        var applicationId = await CreateApplicationAsync();
        var interview = await CreateAsync(applicationId, Input());
        var before = (await EventsAsync(applicationId)).Count;

        await UpdateAsync(interview!.Id, Input(new DateTime(2026, 10, 29, 9, 0, 0), InterviewStatus.Abgesagt));

        Assert.Equal(before + 2, (await EventsAsync(applicationId)).Count);
    }

    [Fact]
    public async Task Update_NonexistentInterview_ReturnsNull()
    {
        using var db = _database.CreateContext();

        Assert.Null(await Service(db).UpdateAsync(999, Input()));
    }

    // ---------- Delete ----------

    [Fact]
    public async Task Delete_RemovesInterviewAndAddsHistoryEntry()
    {
        var applicationId = await CreateApplicationAsync();
        var interview = await CreateAsync(applicationId, Input());

        using (var db = _database.CreateContext())
        {
            Assert.True(await Service(db).DeleteAsync(interview!.Id));
        }

        using var readDb = _database.CreateContext();
        Assert.Empty(await readDb.Interviews.ToListAsync());
        Assert.Single(await EventsAsync(applicationId, EventType.InterviewDeleted));
    }

    [Fact]
    public async Task Delete_NonexistentInterview_ReturnsFalse()
    {
        using var db = _database.CreateContext();

        Assert.False(await Service(db).DeleteAsync(999));
    }

    [Fact]
    public async Task DeletingApplication_DeletesItsInterviews()
    {
        var applicationId = await CreateApplicationAsync();
        await CreateAsync(applicationId, Input());

        using (var db = _database.CreateContext())
        {
            await new ApplicationService(db, _clock, _database.Storage).DeleteAsync(applicationId);
        }

        using var readDb = _database.CreateContext();
        Assert.Empty(await readDb.Interviews.ToListAsync());
    }
}
