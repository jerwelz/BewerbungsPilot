using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Rebuilds the RECORDED status history ("Erfasster Statusverlauf") of applications
// from the history table – for the "Verlauf" page.
//
// What the data can and cannot say:
// - Status changes carry the time they were RECORDED in BewerbungsPilot, not the time
//   the employer did something. Segments are "time in a recorded status".
// - AppliedOn is the submission date the USER entered (a real date, no time).
// - Interviews are appointments (local wall-clock time), deadlines are due dates.
// - Nothing is invented: if the history is incomplete, the affected part is marked
//   as uncertain instead of filling in guessed stages.
public class TimelineService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public TimelineService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    // The order of the pipeline. Moving to an earlier stage is a "backward" transition.
    // Absage is last: leaving it (e.g. reopening) also counts as backward.
    public static bool IsBackward(ApplicationStatus from, ApplicationStatus to) => to < from;

    // Only Created and StatusChanged entries describe statuses.
    public static bool IsStatusEvent(ApplicationEvent e) =>
        (e.Type == EventType.Created || e.Type == EventType.StatusChanged) && e.ToStatus is not null;

    // All applications whose recorded history overlaps the range (local days, both included).
    // Without a range, everything is returned.
    public async Task<TimelineResponse> GetTimelineAsync(DateOnly? from = null, DateOnly? to = null)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var zone = _clock.LocalTimeZone;
        DateTime? rangeStart = from is { } f ? ToUtc(f, zone) : null;
        DateTime? rangeEnd = to is { } t ? ToUtc(t.AddDays(1), zone) : null; // exclusive

        var applications = await _db.Applications.AsNoTracking().ToListAsync();
        var statusEvents = (await _db.ApplicationEvents.AsNoTracking()
                .Where(e => e.Type == EventType.Created || e.Type == EventType.StatusChanged)
                .ToListAsync())
            .ToLookup(e => e.JobApplicationId);
        var interviews = (await _db.Interviews.AsNoTracking().ToListAsync()).ToLookup(i => i.JobApplicationId);

        var journeys = new List<JourneyResponse>();
        foreach (var application in applications)
        {
            var journey = BuildJourney(application, statusEvents[application.Id], interviews[application.Id], now, zone);
            var clipped = rangeStart is null && rangeEnd is null ? journey : ClipToRange(journey, rangeStart, rangeEnd, now);
            if (clipped is not null)
                journeys.Add(clipped);
        }

        return new TimelineResponse(now, rangeStart, rangeEnd, journeys);
    }

    // Reconstructs one application's recorded history. Pure function → easy to test.
    public static JourneyResponse BuildJourney(JobApplication application, IEnumerable<ApplicationEvent> events,
        IEnumerable<Interview> interviews, DateTime now, TimeZoneInfo zone)
    {
        // Several entries in the same second keep the order in which they were saved (Id).
        var ordered = events.Where(IsStatusEvent).OrderBy(e => e.OccurredAt).ThenBy(e => e.Id).ToList();
        var segments = new List<StatusSegment>();
        var transitions = new List<TransitionMarker>();
        var complete = true;
        var day0 = application.AppliedOn;

        double? Day(DateTime utc) => day0 is { } d ? DaysBetween(d, TimeZoneInfo.ConvertTimeFromUtc(utc, zone)) : null;
        StatusSegment Segment(ApplicationStatus status, DateTime start, bool uncertain) =>
            new(status, start, null, uncertain, Day(start), null);
        void CloseLast(DateTime end) => segments[^1] = segments[^1] with { End = end, EndDay = Day(end) };

        if (ordered.Count == 0)
        {
            // No recorded history at all (e.g. data from before history tracking):
            // only the current status is known – since the last recorded change.
            complete = false;
            segments.Add(Segment(application.Status, application.StatusChangedAt, uncertain: true));
        }
        else
        {
            var first = ordered[0];
            if (first.Type != EventType.Created)
            {
                // The start of the history is missing. The status before the first recorded
                // change is known (FromStatus), but not when it began → uncertain.
                complete = false;
                if (first.FromStatus is { } before)
                    segments.Add(Segment(before, application.CreatedAt, uncertain: true));
            }

            foreach (var e in ordered)
            {
                var to = e.ToStatus!.Value;
                if (e.Type == EventType.StatusChanged && segments.Count > 0)
                {
                    // A change "from X" that does not match the previous status means
                    // something in between was not recorded.
                    if (e.FromStatus is { } fromStatus && fromStatus != segments[^1].Status)
                        complete = false;
                    CloseLast(e.OccurredAt);
                }

                var isInitial = e.Type == EventType.Created;
                transitions.Add(new TransitionMarker(e.OccurredAt, isInitial ? null : e.FromStatus, to,
                    !isInitial && e.FromStatus is { } f && IsBackward(f, to), isInitial, Day(e.OccurredAt)));
                segments.Add(Segment(to, e.OccurredAt, uncertain: false));
            }

            // The recorded history must end in the current status; otherwise a change is missing.
            if (segments[^1].Status != application.Status)
            {
                complete = false;
                var since = application.StatusChangedAt > segments[^1].Start ? application.StatusChangedAt : now;
                CloseLast(since);
                segments.Add(Segment(application.Status, since, uncertain: true));
            }
        }

        var interviewMarkers = interviews
            .OrderBy(i => i.ScheduledAt)
            .Select(i => new InterviewMarker(i.Id, i.ScheduledAt, i.Type, i.Status,
                day0 is { } d ? DaysBetween(d, i.ScheduledAt) : null))
            .ToList();

        return new JourneyResponse(
            application.Id, application.CompanyName, application.Position, application.Status,
            application.AppliedOn, application.Deadline, application.CreatedAt,
            HistoryComplete: complete,
            StartsBeforeRange: false,
            CurrentDay: Day(now),
            DeadlineDay: day0 is { } dd && application.Deadline is { } deadline ? deadline.DayNumber - dd.DayNumber : null,
            segments, transitions, interviewMarkers);
    }

    // Keeps only what overlaps the range. The segment that was active at the start of
    // the range is kept with its real start ("last known state before the range").
    // Returns null if nothing of the application falls into the range.
    public static JourneyResponse? ClipToRange(JourneyResponse journey, DateTime? rangeStart, DateTime? rangeEnd, DateTime now)
    {
        var start = rangeStart ?? DateTime.MinValue;
        var end = rangeEnd ?? DateTime.MaxValue;

        var segments = journey.Segments.Where(s => s.Start < end && (s.End ?? now) > start).ToList();
        if (segments.Count == 0)
            return null;

        return journey with
        {
            Segments = segments,
            StartsBeforeRange = segments[0].Start < start,
            Transitions = journey.Transitions.Where(t => t.RecordedAt >= start && t.RecordedAt < end).ToList()
        };
    }

    // Days from local midnight of the submission date to a local date/time.
    // Uses wall-clock times, so a daylight saving change does not create "23-hour days".
    public static double DaysBetween(DateOnly day0, DateTime localDateTime) =>
        (DateTime.SpecifyKind(localDateTime, DateTimeKind.Unspecified) - day0.ToDateTime(TimeOnly.MinValue)).TotalDays;

    private static DateTime ToUtc(DateOnly localDay, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(localDay.ToDateTime(TimeOnly.MinValue), zone);
}
