using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Calculates all dashboard numbers from the real data in the database.
public class DashboardService
{
    public const int MonthsShown = 6;
    public const int RecentActivityCount = 8;

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public DashboardService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<DashboardResponse> GetDashboardAsync()
    {
        var applications = await _db.Applications.ToListAsync();
        var today = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

        int Count(params ApplicationStatus[] statuses) =>
            applications.Count(a => statuses.Contains(a.Status));

        var total = applications.Count;
        var submitted = total - Count(ApplicationStatus.Entwurf);
        // "Got a response" = the company reacted with an interview, an offer or a rejection.
        var responded = Count(ApplicationStatus.Vorstellungsgespraech, ApplicationStatus.Angebot, ApplicationStatus.Absage);

        var recentEvents = await _db.ApplicationEvents
            .Include(e => e.JobApplication)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Take(RecentActivityCount)
            .ToListAsync();

        return new DashboardResponse(
            Total: total,
            Drafts: Count(ApplicationStatus.Entwurf),
            InProgress: Count(ApplicationStatus.Beworben, ApplicationStatus.InPruefung),
            Interviews: Count(ApplicationStatus.Vorstellungsgespraech),
            Offers: Count(ApplicationStatus.Angebot),
            Rejections: Count(ApplicationStatus.Absage),
            ResponseRatePercent: Percent(responded, submitted),
            StatusDistribution: Enum.GetValues<ApplicationStatus>()
                .Select(s => new StatusCount(s, Count(s), Percent(Count(s), total)))
                .ToList(),
            MonthlyActivity: CountPerMonth(applications, today),
            RecentActivity: recentEvents
                .Select(e => new ActivityItem(e.JobApplicationId, e.JobApplication!.CompanyName,
                    e.JobApplication.Position, e.Type, e.Description, e.OccurredAt))
                .ToList());
    }

    // Number of submitted applications (by AppliedOn) for each of the last months,
    // including months with zero applications, oldest month first.
    public static List<MonthCount> CountPerMonth(IEnumerable<JobApplication> applications, DateOnly today)
    {
        var result = new List<MonthCount>();
        var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-(MonthsShown - 1));

        for (var i = 0; i < MonthsShown; i++)
        {
            var month = firstMonth.AddMonths(i);
            var count = applications.Count(a =>
                a.AppliedOn is { } date && date.Year == month.Year && date.Month == month.Month);
            result.Add(new MonthCount(month.Year, month.Month, count));
        }
        return result;
    }

    // Returns null instead of dividing by zero.
    public static int? Percent(int part, int whole) =>
        whole == 0 ? null : (int)Math.Round(100.0 * part / whole);
}
