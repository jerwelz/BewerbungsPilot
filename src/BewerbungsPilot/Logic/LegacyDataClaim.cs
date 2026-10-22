using System.Security.Cryptography;
using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Assigns the data from before user accounts existed (Applications.OwnerId = NULL) to ONE account.
// Only through the command line on the computer that holds the data:
//   dotnet run --project src/BewerbungsPilot -- claim-legacy-data --email you@example.com
// Never automatic: registering an account – also the first one – does NOT give it the old data.
//
// The ONLY place in the application that ignores the query filters and uses ExecuteUpdate
// (checked by SecurityConventionTests). Must not be called from an HTTP endpoint.
public class LegacyDataClaim
{
    private readonly AppDbContext _db;
    private readonly DocumentStorage _storage;

    public LegacyDataClaim(AppDbContext db, DocumentStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record Summary(int Applications, int Events, int Documents, int Interviews,
        int Deadlines, int ConfirmedLocations, int FilesOk, List<string> FileProblems);

    public static Task<int> CountUnclaimedAsync(AppDbContext db) =>
        db.Applications.IgnoreQueryFilters().CountAsync(a => a.OwnerId == null);

    // ownerId null = the unclaimed data.
    public async Task<Summary> GetSummaryAsync(string? ownerId)
    {
        var applications = _db.Applications.IgnoreQueryFilters().Where(a => a.OwnerId == ownerId);
        var documents = await _db.Documents.IgnoreQueryFilters()
            .Where(d => d.JobApplication!.OwnerId == ownerId)
            .Select(d => new { d.Id, d.StoredFileName, d.Sha256 })
            .ToListAsync();

        var problems = new List<string>();
        foreach (var document in documents)
        {
            using var stream = _storage.OpenRead(document.StoredFileName);
            if (stream is null)
                problems.Add($"Dokument {document.Id}: Datei fehlt");
            else if (Convert.ToHexStringLower(SHA256.HashData(stream)) != document.Sha256)
                problems.Add($"Dokument {document.Id}: SHA-256 weicht ab");
        }

        return new Summary(
            await applications.CountAsync(),
            await _db.ApplicationEvents.IgnoreQueryFilters().CountAsync(e => e.JobApplication!.OwnerId == ownerId),
            documents.Count,
            await _db.Interviews.IgnoreQueryFilters().CountAsync(i => i.JobApplication!.OwnerId == ownerId),
            await applications.CountAsync(a => a.Deadline != null),
            await applications.CountAsync(a => a.MapLatitude != null || a.LocationPrecision == LocationPrecision.Remote),
            documents.Count - problems.Count,
            problems);
    }

    // Assigns all unclaimed applications (and with them their history, documents and
    // interviews) to the account in ONE transaction and checks the result before committing.
    // beforeValidation: test hook only.
    public async Task<Summary> ClaimAsync(string userId, Func<Task>? beforeValidation = null)
    {
        if (!await _db.Users.AnyAsync(u => u.Id == userId))
            throw new InvalidOperationException("Dieses Konto existiert nicht.");

        var unclaimed = await GetSummaryAsync(null);
        var ownedBefore = await GetSummaryAsync(userId);

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var updated = await _db.Applications.IgnoreQueryFilters()
            .Where(a => a.OwnerId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.OwnerId, userId));
        if (beforeValidation is not null)
            await beforeValidation();

        var ownedAfter = await GetSummaryAsync(userId);
        var remaining = await CountUnclaimedAsync(_db);
        var consistent = updated == unclaimed.Applications && remaining == 0
            && ownedAfter.Applications == ownedBefore.Applications + unclaimed.Applications
            && ownedAfter.Events == ownedBefore.Events + unclaimed.Events
            && ownedAfter.Documents == ownedBefore.Documents + unclaimed.Documents
            && ownedAfter.Interviews == ownedBefore.Interviews + unclaimed.Interviews
            && ownedAfter.Deadlines == ownedBefore.Deadlines + unclaimed.Deadlines
            && ownedAfter.ConfirmedLocations == ownedBefore.ConfirmedLocations + unclaimed.ConfirmedLocations;
        if (!consistent)
        {
            await transaction.RollbackAsync();
            throw new InvalidOperationException("Die Prüfung nach der Zuordnung ist fehlgeschlagen. Es wurde nichts geändert.");
        }

        await transaction.CommitAsync();
        return unclaimed;
    }
}
