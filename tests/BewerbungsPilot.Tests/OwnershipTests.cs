using System.Text.Json;
using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Data isolation between users (AppDbContext: query filters + ownership guard).
// "Now" in all tests: Thursday, 22.10.2026, 09:00.
public class OwnershipTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private const string UserA = TestDatabase.UserA;
    private const string UserB = TestDatabase.UserB;
    private static readonly byte[] Pdf = "%PDF-1.4\n% Testdokument\n"u8.ToArray();

    public void Dispose() => _database.Dispose();

    private sealed record Seeded(int ApplicationId, int InterviewId, int DocumentId, int EventId);

    // One application with everything: deadline, confirmed location, interview, document, history.
    private async Task<Seeded> SeedAsync(string userId, string company)
    {
        using var db = _database.CreateContext(userId);
        var applications = new ApplicationService(db, _clock, _database.Storage);
        var application = await applications.CreateAsync(new ApplicationInput(company, "Auszubildender FIAE", "Hamburg", null,
            new DateOnly(2026, 11, 5), new DateOnly(2026, 10, 1), ApplicationStatus.Beworben, $"Geheime Notiz {company}"));
        await new MapService(db).SetLocationAsync(application.Id, new MapLocationInput(LocationPrecision.City, 53.55, 9.99, $"Ort {company}"));
        var interview = await new InterviewService(db, _clock).CreateAsync(application.Id,
            new InterviewInput(new DateTime(2026, 10, 28, 14, 30, 0), InterviewType.Video, "https://meet.example.org", $"Kontakt {company}", null));
        var document = await new DocumentService(db, _database.Storage, _clock).UploadAsync(application.Id, "Lebenslauf", "cv.pdf", Pdf);
        await applications.ChangeStatusAsync(application.Id, ApplicationStatus.Vorstellungsgespraech);
        var eventId = await db.ApplicationEvents.Where(e => e.JobApplicationId == application.Id).Select(e => e.Id).FirstAsync();
        return new Seeded(application.Id, interview!.Id, document!.Id, eventId);
    }

    // Everything user A can see about the application, as JSON – to prove "unchanged".
    private async Task<string> SnapshotAsync(int applicationId, string userId = UserA)
    {
        using var db = _database.CreateContext(userId);
        var application = await new ApplicationService(db, _clock, _database.Storage).GetByIdAsync(applicationId);
        return JsonSerializer.Serialize(application, new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles });
    }

    private int StoredFileCount() => Directory.GetFiles(_database.Storage.Directory).Length;

    // ---------- Ownership on create ----------

    [Fact]
    public async Task Create_AssignsCurrentUserAsOwner()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        using var db = _database.CreateContext(UserA);
        Assert.Equal(UserA, (await db.Applications.SingleAsync(x => x.Id == a.ApplicationId)).OwnerId);
    }

    [Fact]
    public async Task NoUser_SeesNothing_AndCannotWrite()
    {
        await SeedAsync(UserA, "Nordlicht GmbH");
        using var db = _database.CreateContext(userId: null);
        Assert.Empty(await db.Applications.ToListAsync());
        Assert.Empty(await db.ApplicationEvents.ToListAsync());
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Empty(await db.Interviews.ToListAsync());

        var service = new ApplicationService(db, _clock, _database.Storage);
        await Assert.ThrowsAsync<OwnershipViolationException>(() => service.CreateAsync(
            new ApplicationInput("Ohne Konto AG", "Azubi", null, null, null, null, ApplicationStatus.Entwurf, null)));
    }

    // ---------- Reads, including every aggregate ----------

    [Fact]
    public async Task OtherUser_SeesNoneOfTheData_InAnyListOrAggregate()
    {
        await SeedAsync(UserA, "Nordlicht GmbH");
        await SeedAsync(UserB, "Hafen Logistik AG");

        using var db = _database.CreateContext(UserB);
        var results = new List<object>
        {
            await new ApplicationService(db, _clock, _database.Storage).GetAllAsync(),
            await new ApplicationService(db, _clock, _database.Storage).FindDuplicatesAsync("Nordlicht GmbH", "Auszubildender FIAE", null),
            await new DashboardService(db, _clock).GetDashboardAsync(),
            await new ScheduleService(db, _clock).GetEventsAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30)),
            await new ScheduleService(db, _clock).GetUpcomingAsync(),
            await new TimelineService(db, _clock).GetTimelineAsync(),
            await new WaitingQueueService(db, _clock).GetQueueAsync(WaitingSort.SinceSubmission),
            await new AnalysisService(db, _clock).GetAnalysisAsync(null, null, null),
            await new MapService(db).GetMapAsync(53.55, 9.99),
        };

        var options = new JsonSerializerOptions { ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles };
        foreach (var result in results)
        {
            var json = JsonSerializer.Serialize(result, options);
            Assert.DoesNotContain("Nordlicht", json);
            Assert.DoesNotContain("Notiz Nordlicht", json);
            Assert.DoesNotContain("Ort Nordlicht", json);
        }

        Assert.Single(await db.Applications.ToListAsync());
        var dashboard = await new DashboardService(db, _clock).GetDashboardAsync();
        Assert.Equal(1, dashboard.Total);
        Assert.Contains(await new MapService(db).GetMapAsync(null, null) is { } map ? map.Items : [], i => i.CompanyName == "Hafen Logistik AG");
    }

    [Fact]
    public async Task OtherUser_CannotLoadRecordsById()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        using var db = _database.CreateContext(UserB);
        Assert.Null(await new ApplicationService(db, _clock, _database.Storage).GetByIdAsync(a.ApplicationId));
        Assert.Null(await new InterviewService(db, _clock).GetAsync(a.InterviewId));
        Assert.Null(await new DocumentService(db, _database.Storage, _clock).GetAsync(a.DocumentId));
        Assert.Empty(await new DocumentService(db, _database.Storage, _clock).GetForApplicationAsync(a.ApplicationId));
        Assert.Null(await db.Applications.FindAsync(a.ApplicationId));
        Assert.Null(await db.ApplicationEvents.FindAsync(a.EventId));
    }

    [Fact]
    public async Task FilterValue_IsReadPerContext_NotCachedForTheFirstUser()
    {
        await SeedAsync(UserA, "Nordlicht GmbH");
        await SeedAsync(UserB, "Hafen Logistik AG");
        foreach (var (user, company) in new[] { (UserA, "Nordlicht GmbH"), (UserB, "Hafen Logistik AG"), (UserA, "Nordlicht GmbH") })
        {
            using var db = _database.CreateContext(user);
            Assert.Equal(company, (await db.Applications.SingleAsync()).CompanyName);
            Assert.All(await db.Interviews.Include(i => i.JobApplication).ToListAsync(), i => Assert.Equal(user, i.JobApplication!.OwnerId));
        }
    }

    // ---------- Writes through the services with another user's ids ----------

    [Fact]
    public async Task OtherUser_ServiceWrites_ReturnNotFound_AndChangeNothing()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        var before = await SnapshotAsync(a.ApplicationId);
        var files = StoredFileCount();

        using (var db = _database.CreateContext(UserB))
        {
            var applications = new ApplicationService(db, _clock, _database.Storage);
            Assert.Null(await applications.UpdateAsync(a.ApplicationId, new ApplicationInput("Gehackt", "x", null, null, null, null, ApplicationStatus.Absage, null)));
            Assert.Null(await applications.ChangeStatusAsync(a.ApplicationId, ApplicationStatus.Absage));
            Assert.False(await applications.DeleteAsync(a.ApplicationId));
            Assert.Null(await new MapService(db).SetLocationAsync(a.ApplicationId, new MapLocationInput(LocationPrecision.City, 1, 1, "x")));

            var interviews = new InterviewService(db, _clock);
            Assert.Null(await interviews.CreateAsync(a.ApplicationId, new InterviewInput(new DateTime(2026, 11, 1, 10, 0, 0), InterviewType.Telefon, null, null, null)));
            Assert.Null(await interviews.UpdateAsync(a.InterviewId, new InterviewInput(new DateTime(2026, 11, 1, 10, 0, 0), InterviewType.Telefon, null, null, "x")));
            Assert.False(await interviews.DeleteAsync(a.InterviewId));

            var documents = new DocumentService(db, _database.Storage, _clock);
            Assert.Null(await documents.UploadAsync(a.ApplicationId, "Zeugnis", "fremd.pdf", Pdf));
            Assert.False(await documents.DeleteAsync(a.DocumentId));
        }

        Assert.Equal(before, await SnapshotAsync(a.ApplicationId));
        Assert.Equal(files, StoredFileCount());
    }

    // ---------- Unauthorized child-record modifications directly on the context ----------
    // Each attempt must throw before anything is written; user A's data stays exactly the same.

    public static TheoryData<string> ChildAttacks =>
    [
        "add event with foreign application id",           // the InterviewService.AddEvent pattern
        "add interview with foreign application id",
        "add document with foreign application id",
        "move own interview to foreign application",
        "move own document to foreign application",
        "modify attached foreign interview",
        "modify foreign event via State = Modified",
        "delete attached foreign document",
        "delete attached foreign interview",
        "delete attached foreign event",
        "modify attached foreign application",
        "delete attached foreign application",
        "create application owned by somebody else",
        "change owner of own application",
    ];

    [Theory]
    [MemberData(nameof(ChildAttacks))]
    public async Task OtherUser_DirectContextManipulation_IsRejected(string attack)
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        var b = await SeedAsync(UserB, "Hafen Logistik AG");
        var before = await SnapshotAsync(a.ApplicationId);
        var beforeB = await SnapshotAsync(b.ApplicationId, UserB);
        var now = new DateTime(2026, 10, 22, 7, 0, 0, DateTimeKind.Utc);

        using (var db = _database.CreateContext(UserB))
        {
            switch (attack)
            {
                case "add event with foreign application id":
                    db.ApplicationEvents.Add(new ApplicationEvent { JobApplicationId = a.ApplicationId, Type = EventType.StatusChanged, Description = "x", OccurredAt = now });
                    break;
                case "add interview with foreign application id":
                    db.Interviews.Add(new Interview { JobApplicationId = a.ApplicationId, ScheduledAt = new DateTime(2026, 11, 1, 10, 0, 0), CreatedAt = now, UpdatedAt = now });
                    break;
                case "add document with foreign application id":
                    db.Documents.Add(new ApplicationDocument { JobApplicationId = a.ApplicationId, OriginalFileName = "x.pdf", StoredFileName = $"{Guid.NewGuid():N}.pdf", ContentType = "application/pdf", Sha256 = "", UploadedAt = now });
                    break;
                case "move own interview to foreign application":
                    (await db.Interviews.SingleAsync(i => i.Id == b.InterviewId)).JobApplicationId = a.ApplicationId;
                    break;
                case "move own document to foreign application":
                    (await db.Documents.SingleAsync(d => d.Id == b.DocumentId)).JobApplicationId = a.ApplicationId;
                    break;
                case "modify attached foreign interview":
                {
                    var forged = new Interview { Id = a.InterviewId, JobApplicationId = b.ApplicationId };
                    db.Interviews.Attach(forged);
                    forged.Notes = "gehackt";
                    break;
                }
                case "modify foreign event via State = Modified":
                    db.Entry(new ApplicationEvent { Id = a.EventId, JobApplicationId = b.ApplicationId, Description = "gehackt", OccurredAt = now }).State = EntityState.Modified;
                    break;
                case "delete attached foreign document":
                    db.Documents.Remove(new ApplicationDocument { Id = a.DocumentId, JobApplicationId = b.ApplicationId });
                    break;
                case "delete attached foreign interview":
                    db.Interviews.Remove(new Interview { Id = a.InterviewId, JobApplicationId = b.ApplicationId });
                    break;
                case "delete attached foreign event":
                    db.ApplicationEvents.Remove(new ApplicationEvent { Id = a.EventId, JobApplicationId = b.ApplicationId });
                    break;
                case "modify attached foreign application":
                {
                    var forged = new JobApplication { Id = a.ApplicationId, OwnerId = UserB };
                    db.Applications.Attach(forged);
                    forged.Notes = "gehackt";
                    break;
                }
                case "delete attached foreign application":
                    db.Applications.Remove(new JobApplication { Id = a.ApplicationId, OwnerId = UserB });
                    break;
                case "create application owned by somebody else":
                    db.Applications.Add(new JobApplication { CompanyName = "x", Position = "x", OwnerId = UserA, CreatedAt = now, UpdatedAt = now, StatusChangedAt = now });
                    break;
                case "change owner of own application":
                    (await db.Applications.SingleAsync(x => x.Id == b.ApplicationId)).OwnerId = UserA;
                    break;
            }

            await Assert.ThrowsAsync<OwnershipViolationException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(before, await SnapshotAsync(a.ApplicationId));
        Assert.Equal(beforeB, await SnapshotAsync(b.ApplicationId, UserB));
        using var check = _database.CreateContext(UserA);
        Assert.Single(await check.Applications.ToListAsync());
    }

    [Fact]
    public async Task SynchronousSaveChanges_IsGuardedToo()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        using var db = _database.CreateContext(UserB);
        db.ApplicationEvents.Add(new ApplicationEvent { JobApplicationId = a.ApplicationId, Description = "x", OccurredAt = DateTime.UtcNow });
        Assert.Throws<OwnershipViolationException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task Owner_CanDoTheSameChildOperations()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        using (var db = _database.CreateContext(UserA))
        {
            db.ApplicationEvents.Add(new ApplicationEvent { JobApplicationId = a.ApplicationId, Type = EventType.StatusChanged, Description = "eigener Eintrag", OccurredAt = DateTime.UtcNow });
            (await db.Interviews.SingleAsync(i => i.Id == a.InterviewId)).Notes = "eigene Notiz";
            db.ApplicationEvents.Remove(await db.ApplicationEvents.SingleAsync(e => e.Id == a.EventId));
            await db.SaveChangesAsync();
        }

        using var check = _database.CreateContext(UserA);
        Assert.Equal("eigene Notiz", (await check.Interviews.SingleAsync()).Notes);
        Assert.Contains(await check.ApplicationEvents.ToListAsync(), e => e.Description == "eigener Eintrag");
        Assert.DoesNotContain(await check.ApplicationEvents.ToListAsync(), e => e.Id == a.EventId);
    }

    [Fact]
    public async Task Owner_CanDeleteApplication_WithAllChildren()
    {
        var a = await SeedAsync(UserA, "Nordlicht GmbH");
        var b = await SeedAsync(UserB, "Hafen Logistik AG");
        using (var db = _database.CreateContext(UserA))
            Assert.True(await new ApplicationService(db, _clock, _database.Storage).DeleteAsync(a.ApplicationId));

        using var checkB = _database.CreateContext(UserB);
        Assert.NotNull(await new DocumentService(checkB, _database.Storage, _clock).GetAsync(b.DocumentId));
        Assert.Equal(1, StoredFileCount()); // only B's file is left
    }
}
