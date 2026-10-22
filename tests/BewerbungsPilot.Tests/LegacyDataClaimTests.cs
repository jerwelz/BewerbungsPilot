using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Claiming the data from before user accounts existed (LegacyDataClaim.cs).
public class LegacyDataClaimTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private static readonly byte[] Pdf = "%PDF-1.4\n% Altbestand\n"u8.ToArray();

    public void Dispose() => _database.Dispose();

    // Creates applications as user A, then turns some of them into "legacy" rows (OwnerId NULL),
    // exactly what the AddIdentityAndOwnership migration leaves behind.
    private async Task<List<int>> CreateLegacyAsync(int count, bool withChildren = true)
    {
        var ids = new List<int>();
        using var db = _database.CreateContext(TestDatabase.UserA);
        for (var i = 0; i < count; i++)
        {
            var application = await new ApplicationService(db, _clock, _database.Storage).CreateAsync(new ApplicationInput(
                $"Altfirma {i}", "Azubi", "Kiel", null, new DateOnly(2026, 11, 1), null, ApplicationStatus.Beworben, null));
            if (withChildren)
            {
                await new DocumentService(db, _database.Storage, _clock).UploadAsync(application.Id, "Lebenslauf", "cv.pdf", Pdf);
                await new InterviewService(db, _clock).CreateAsync(application.Id,
                    new InterviewInput(new DateTime(2026, 11, 3, 10, 0, 0), InterviewType.Telefon, null, null, null));
            }
            ids.Add(application.Id);
        }
        foreach (var id in ids)
            await db.Database.ExecuteSqlAsync($"UPDATE Applications SET OwnerId = NULL WHERE Id = {id}");
        return ids;
    }

    private LegacyDataClaim Claim(AppDbContext db) => new(db, _database.Storage);

    [Fact]
    public async Task Claim_AssignsAllLegacyData_ToTheNamedAccountOnly()
    {
        using (var db = _database.CreateContext(TestDatabase.UserA))
            await new ApplicationService(db, _clock, _database.Storage).CreateAsync(new ApplicationInput(
                "Eigene Firma", "Azubi", null, null, null, null, ApplicationStatus.Entwurf, null));
        await CreateLegacyAsync(2);

        using (var admin = _database.CreateContext(userId: null))
        {
            Assert.Equal(2, await LegacyDataClaim.CountUnclaimedAsync(admin));
            var summary = await Claim(admin).ClaimAsync(TestDatabase.UserB);
            Assert.Equal((2, 6, 2, 2, 2, 2), (summary.Applications, summary.Events, summary.Documents, summary.Interviews, summary.FilesOk, summary.Deadlines));
            Assert.Equal(0, await LegacyDataClaim.CountUnclaimedAsync(admin));
        }

        using var userB = _database.CreateContext(TestDatabase.UserB);
        Assert.Equal(2, await userB.Applications.CountAsync());
        Assert.Equal(2, await userB.Documents.CountAsync());
        Assert.Equal(2, await userB.Interviews.CountAsync());
        using var userA = _database.CreateContext(TestDatabase.UserA);
        Assert.Equal("Eigene Firma", (await userA.Applications.SingleAsync()).CompanyName); // A keeps its own, gets nothing
    }

    [Fact]
    public async Task Claim_UnknownAccount_ChangesNothing()
    {
        await CreateLegacyAsync(1);
        using var admin = _database.CreateContext(userId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Claim(admin).ClaimAsync("no-such-user"));
        Assert.Equal(1, await LegacyDataClaim.CountUnclaimedAsync(admin));
    }

    [Fact]
    public async Task Claim_IsIdempotent()
    {
        await CreateLegacyAsync(1);
        using var admin = _database.CreateContext(userId: null);
        await Claim(admin).ClaimAsync(TestDatabase.UserB);
        var second = await Claim(admin).ClaimAsync(TestDatabase.UserA);
        Assert.Equal(0, second.Applications);
        using var userB = _database.CreateContext(TestDatabase.UserB);
        Assert.Equal(1, await userB.Applications.CountAsync());
    }

    [Fact]
    public async Task Claim_FailedValidation_RollsEverythingBack()
    {
        var ids = await CreateLegacyAsync(3, withChildren: false);
        using var admin = _database.CreateContext(userId: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Claim(admin).ClaimAsync(TestDatabase.UserB, () =>
            admin.Database.ExecuteSqlAsync($"UPDATE Applications SET OwnerId = NULL WHERE Id = {ids[0]}"))); // sabotage inside the transaction
        Assert.Equal(3, await LegacyDataClaim.CountUnclaimedAsync(admin)); // not 1: the whole claim was rolled back
    }

    [Fact]
    public async Task Summary_ReportsMissingDocumentFiles()
    {
        await CreateLegacyAsync(1);
        foreach (var file in Directory.GetFiles(_database.Storage.Directory))
            File.Delete(file);
        using var admin = _database.CreateContext(userId: null);
        var summary = await Claim(admin).GetSummaryAsync(null);
        Assert.Equal(0, summary.FilesOk);
        Assert.Single(summary.FileProblems);
    }

    [Fact]
    public async Task RegisteringAnAccount_DoesNotGiveItLegacyData()
    {
        await CreateLegacyAsync(1);
        using var userB = _database.CreateContext(TestDatabase.UserB);
        Assert.Empty(await userB.Applications.ToListAsync());
    }
}
