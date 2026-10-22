using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

public enum Milestone
{
    Submitted,  // Bewerbung eingereicht
    Interview,  // Vorstellungsgespräch erreicht
    Offer       // Angebot erhalten
}

public enum EvidenceSource
{
    SubmissionDate,    // the submission date the user entered
    StatusRecorded,    // a status entry in the history
    CurrentStatus,     // the application's current status (recorded at StatusChangedAt)
    InterviewRecorded  // an interview entry (planned or completed)
}

// "Analyse": how many DISTINCT applications have documented evidence of reaching each milestone.
//
// Rules (the explanation on the page says the same):
// - Cohort: applications whose ENTERED submission date lies in the selected period and on or
//   before the observation cutoff. Applications without submission date are reported
//   separately – the creation date is never used instead.
// - Evidence only counts if it was RECORDED on or before the cutoff (end of that local day).
// - Interview: a recorded status "Vorstellungsgespräch" or an interview entry that is not
//   cancelled. Several interviews = one application. A later rejection keeps the milestone.
// - Offer: a recorded status "Angebot". An offer never implies an interview.
// - Missing evidence means "not documented", NOT "did not happen". Milestones are counted
//   independently – there is no forced funnel and nothing is predicted.
public class AnalysisService
{
    public const int MinCohortForPercent = 10; // below this, percentages would exaggerate

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public AnalysisService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public async Task<AnalysisResponse> GetAnalysisAsync(DateOnly? from, DateOnly? to, DateOnly? cutoff)
    {
        var applications = await _db.Applications.AsNoTracking().ToListAsync();
        var events = (await _db.ApplicationEvents.AsNoTracking()
                .Where(e => e.Type == EventType.Created || e.Type == EventType.StatusChanged)
                .ToListAsync())
            .ToLookup(e => e.JobApplicationId);
        var interviews = (await _db.Interviews.AsNoTracking().ToListAsync()).ToLookup(i => i.JobApplicationId);

        return Analyze(applications, events, interviews, from, to, cutoff ?? Today,
            _clock.GetUtcNow().UtcDateTime, _clock.LocalTimeZone);
    }

    // Pure calculation – everything the page and the drill-down show comes from here.
    public static AnalysisResponse Analyze(
        IEnumerable<JobApplication> applications, ILookup<int, ApplicationEvent> events, ILookup<int, Interview> interviews,
        DateOnly? from, DateOnly? to, DateOnly cutoffDate, DateTime now, TimeZoneInfo zone)
    {
        // Observation cutoff: end of the cutoff day (local), never later than "now".
        var endOfCutoffDay = TimeZoneInfo.ConvertTimeToUtc(cutoffDate.AddDays(1).ToDateTime(TimeOnly.MinValue), zone);
        var cutoff = endOfCutoffDay < now ? endOfCutoffDay : now;

        var cohort = new List<AnalysisApplication>();
        var withoutDate = new List<AnalysisApplication>();

        foreach (var a in applications.OrderBy(a => a.CompanyName, StringComparer.CurrentCultureIgnoreCase))
        {
            // Reuse the Verlauf reconstruction: completeness and status at the cutoff.
            var journey = TimelineService.BuildJourney(a, events[a.Id], [], now, zone);
            var item = new AnalysisApplication(a.Id, a.CompanyName, a.Position, a.Status,
                StatusAt(journey, cutoff), a.AppliedOn, journey.HistoryComplete,
                CollectEvidence(a, events[a.Id], interviews[a.Id], cutoff));

            if (a.AppliedOn is not { } applied)
            {
                // Only applications that existed at the cutoff are mentioned at all.
                if (a.CreatedAt <= cutoff)
                    withoutDate.Add(item);
            }
            else if (applied <= cutoffDate && (from is null || applied >= from) && (to is null || applied <= to))
            {
                cohort.Add(item);
            }
        }

        var milestones = Enum.GetValues<Milestone>().Select(m =>
        {
            var ids = cohort.Where(c => c.Evidence.Any(e => e.Milestone == m)).Select(c => c.Id).ToList();
            return new MilestoneResult(m, ids.Count,
                cohort.Count >= MinCohortForPercent ? DashboardService.Percent(ids.Count, cohort.Count) : null, ids);
        }).ToList();

        var outcomes = new OutcomeSummary(
            Open: cohort.Count(c => c.StatusAtCutoff is { } s && s is not (ApplicationStatus.Angebot or ApplicationStatus.Absage)),
            Offer: cohort.Count(c => c.StatusAtCutoff == ApplicationStatus.Angebot),
            Rejected: cohort.Count(c => c.StatusAtCutoff == ApplicationStatus.Absage),
            Unknown: cohort.Count(c => c.StatusAtCutoff is null));

        return new AnalysisResponse(from, to, cutoffDate, cutoff, cohort.Count, MinCohortForPercent,
            milestones, outcomes, cohort.Count(c => !c.HistoryComplete), cohort,
            new WithoutSubmissionDate(
                Drafts: withoutDate.Count(w => w.CurrentStatus == ApplicationStatus.Entwurf),
                Other: withoutDate.Count(w => w.CurrentStatus != ApplicationStatus.Entwurf),
                Applications: withoutDate));
    }

    // All evidence recorded on or before the cutoff, per milestone.
    public static List<Evidence> CollectEvidence(JobApplication a, IEnumerable<ApplicationEvent> events,
        IEnumerable<Interview> interviews, DateTime cutoff)
    {
        var list = new List<Evidence>();

        if (a.AppliedOn is { } applied)
            list.Add(new Evidence(Milestone.Submitted, EvidenceSource.SubmissionDate, null, applied, null, null, null));

        var statusEntries = events.Where(TimelineService.IsStatusEvent).Where(e => e.OccurredAt <= cutoff)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToList();
        foreach (var (milestone, status) in new[] { (Milestone.Interview, ApplicationStatus.Vorstellungsgespraech), (Milestone.Offer, ApplicationStatus.Angebot) })
        {
            // First recorded time the status was reached is enough as evidence.
            var first = statusEntries.FirstOrDefault(e => e.ToStatus == status);
            if (first is not null)
                list.Add(new Evidence(milestone, EvidenceSource.StatusRecorded, first.OccurredAt, null, status, null, null));
            // The current status is recorded data too (time: StatusChangedAt) – used only
            // if the history itself has no entry for it.
            else if (a.Status == status && a.StatusChangedAt <= cutoff)
                list.Add(new Evidence(milestone, EvidenceSource.CurrentStatus, a.StatusChangedAt, null, status, null, null));
        }

        // Interviews: cancelled ones are not evidence. Recorded = the entry's creation time.
        foreach (var interview in interviews.Where(i => i.Status != InterviewStatus.Abgesagt && i.CreatedAt <= cutoff)
                     .OrderBy(i => i.ScheduledAt))
        {
            list.Add(new Evidence(Milestone.Interview, EvidenceSource.InterviewRecorded, interview.CreatedAt, null,
                null, interview.ScheduledAt, interview.Status));
        }

        return list;
    }

    // Status at the cutoff according to the recorded history; null = not recorded yet.
    public static ApplicationStatus? StatusAt(JourneyResponse journey, DateTime cutoff) =>
        journey.Segments.LastOrDefault(s => s.Start <= cutoff)?.Status;
}
