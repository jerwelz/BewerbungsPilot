using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

public enum WaitingSort
{
    SinceSubmission,   // days since the submission date the user entered
    SinceStatusChange  // days since the last RECORDED status change
}

// "Wartende Bewerbungen": submitted applications without a final outcome, ranked by
// elapsed time. Both measurements are plain elapsed days – neither says anything about
// what the employer is doing, and nothing is predicted.
public class WaitingQueueService
{
    // Submitted and not finished. Entwurf is not submitted yet; Angebot and Absage are outcomes.
    public static readonly ApplicationStatus[] WaitingStatuses =
        [ApplicationStatus.Beworben, ApplicationStatus.InPruefung, ApplicationStatus.Vorstellungsgespraech];

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public WaitingQueueService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<List<WaitingItem>> GetQueueAsync(WaitingSort sort)
    {
        var zone = _clock.LocalTimeZone;
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

        var applications = await _db.Applications.AsNoTracking()
            .Where(a => WaitingStatuses.Contains(a.Status))
            .ToListAsync();
        var ids = applications.Select(a => a.Id).ToList();

        // Last recorded status entry per application (Created or StatusChanged).
        // Note edits create no such entry, so they never reset this clock.
        var lastStatusEntry = (await _db.ApplicationEvents.AsNoTracking()
                .Where(e => ids.Contains(e.JobApplicationId)
                            && (e.Type == EventType.Created || e.Type == EventType.StatusChanged))
                .ToListAsync())
            .GroupBy(e => e.JobApplicationId)
            .ToDictionary(g => g.Key, g => g.Max(e => e.OccurredAt));

        var items = applications.Select(a =>
        {
            DateTime? lastChange = lastStatusEntry.TryGetValue(a.Id, out var at) ? at : null;
            int? sinceChange = lastChange is { } utc
                ? today.DayNumber - DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone)).DayNumber
                : null;
            int? sinceSubmission = a.AppliedOn is { } applied ? today.DayNumber - applied.DayNumber : null;
            return new WaitingItem(a.Id, a.CompanyName, a.Position, a.Status, a.AppliedOn,
                sinceSubmission, lastChange, sinceChange);
        });

        // Longest waiting first; missing values go to the end (never treated as 0).
        return (sort == WaitingSort.SinceSubmission
                ? items.OrderBy(i => i.DaysSinceSubmission is null).ThenByDescending(i => i.DaysSinceSubmission)
                    .ThenByDescending(i => i.DaysSinceStatusChange)
                : items.OrderBy(i => i.DaysSinceStatusChange is null).ThenByDescending(i => i.DaysSinceStatusChange)
                    .ThenByDescending(i => i.DaysSinceSubmission))
            .ThenBy(i => i.CompanyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
