using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Collects deadlines and interviews as one list of calendar events
// (used by the calendar page and the dashboard).
public class ScheduleService
{
    public const int MaxRangeDays = 100;
    public const int UpcomingCount = 8;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ScheduleService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    // All deadlines and interviews from `from` to `to` (both days included), in date order.
    public async Task<List<CalendarEvent>> GetEventsAsync(DateOnly from, DateOnly to)
    {
        var start = from.ToDateTime(TimeOnly.MinValue);
        var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue); // exclusive: midnight after `to`

        var interviews = await _db.Interviews
            .Include(i => i.JobApplication)
            .Where(i => i.ScheduledAt >= start && i.ScheduledAt < end)
            .ToListAsync();

        var deadlines = await _db.Applications
            .Where(a => a.Deadline != null && a.Deadline >= from && a.Deadline <= to)
            .ToListAsync();

        return Sort(interviews.Select(ToEvent).Concat(deadlines.Select(ToEvent)));
    }

    // What needs attention next: planned interviews and deadlines of unsent applications,
    // including overdue ones (they still need action), in date order.
    public async Task<List<CalendarEvent>> GetUpcomingAsync(int count = UpcomingCount)
    {
        var interviews = await _db.Interviews
            .Include(i => i.JobApplication)
            .Where(i => i.Status == InterviewStatus.Geplant)
            .ToListAsync();

        var deadlines = await _db.Applications
            .Where(a => a.Deadline != null && a.Status == ApplicationStatus.Entwurf)
            .ToListAsync();

        return Sort(interviews.Select(ToEvent).Concat(deadlines.Select(ToEvent))).Take(count).ToList();
    }

    // Date order. A deadline has no time; it counts until the end of its day,
    // so it is sorted after the interviews of the same day.
    private static List<CalendarEvent> Sort(IEnumerable<CalendarEvent> events) =>
        events
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Time ?? TimeOnly.MaxValue)
            .ThenBy(e => e.CompanyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private CalendarEvent ToEvent(Interview i) => new(
        CalendarEventKind.Interview,
        DateOnly.FromDateTime(i.ScheduledAt),
        TimeOnly.FromDateTime(i.ScheduledAt),
        ScheduleRules.InterviewState(i.Status, i.ScheduledAt, Today),
        i.JobApplicationId, i.JobApplication!.CompanyName, i.JobApplication.Position, i.JobApplication.Status,
        i.Id, i.Type, i.Status, i.Location);

    private CalendarEvent ToEvent(JobApplication a) => new(
        CalendarEventKind.Deadline,
        a.Deadline!.Value,
        null,
        ScheduleRules.DeadlineState(a.Status, a.Deadline, Today)!.Value,
        a.Id, a.CompanyName, a.Position, a.Status,
        null, null, null, null);
}
