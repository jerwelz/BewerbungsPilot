using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;

namespace BewerbungsPilot;

// DTOs ("data transfer objects") describe the JSON that travels between
// browser and server. They are kept separate from the database entities,
// so the API only exposes what the frontend needs.

// What the browser sends when creating or editing an application.
public record ApplicationInput(
    string? CompanyName,
    string? Position,
    string? Location,
    string? JobUrl,
    DateOnly? Deadline,
    DateOnly? AppliedOn,
    ApplicationStatus Status,
    string? Notes);

// Body of PATCH /api/applications/{id}/status (Kanban board).
public record StatusChangeInput(ApplicationStatus Status);

// What the server sends back.
public record ApplicationResponse(
    int Id,
    string CompanyName,
    string Position,
    string? Location,
    string? JobUrl,
    DateOnly? Deadline,
    DateOnly? AppliedOn,
    ApplicationStatus Status,
    string? Notes,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime StatusChangedAt,
    ScheduleState? DeadlineState) // calculated by ScheduleRules; null = no deadline
{
    public static ApplicationResponse FromEntity(JobApplication a, DateOnly today) => new(
        a.Id, a.CompanyName, a.Position, a.Location, a.JobUrl, a.Deadline, a.AppliedOn,
        a.Status, a.Notes, a.CreatedAt, a.UpdatedAt, a.StatusChangedAt,
        ScheduleRules.DeadlineState(a.Status, a.Deadline, today));
}

// Detail view: the application plus its history, documents and interviews.
public record ApplicationDetailResponse(
    ApplicationResponse Application, List<EventResponse> Events,
    List<DocumentResponse> Documents, List<InterviewResponse> Interviews)
{
    public static ApplicationDetailResponse FromEntity(JobApplication a, DateOnly today) => new(
        ApplicationResponse.FromEntity(a, today),
        a.Events.Select(e => new EventResponse(e.Id, e.Type, e.FromStatus, e.ToStatus, e.Description, e.OccurredAt)).ToList(),
        a.Documents.Select(DocumentResponse.FromEntity).ToList(),
        a.Interviews.Select(i => InterviewResponse.FromEntity(i, today)).ToList());
}

// ---------- Interviews & calendar ----------

// What the browser sends. ScheduledAt is local time, e.g. "2026-10-28T14:30".
public record InterviewInput(
    DateTime? ScheduledAt,
    InterviewType Type,
    string? Location,
    string? ContactPerson,
    string? Notes,
    InterviewStatus Status = InterviewStatus.Geplant);

public record InterviewResponse(
    int Id, int ApplicationId, DateTime ScheduledAt, InterviewType Type, InterviewStatus Status,
    string? Location, string? ContactPerson, string? Notes, ScheduleState State)
{
    public static InterviewResponse FromEntity(Interview i, DateOnly today) => new(
        i.Id, i.JobApplicationId, i.ScheduledAt, i.Type, i.Status, i.Location, i.ContactPerson, i.Notes,
        ScheduleRules.InterviewState(i.Status, i.ScheduledAt, today));
}

public enum CalendarEventKind { Interview, Deadline }

// One entry in the calendar or the dashboard's "Anstehende Termine".
// Time is null for deadlines (they last the whole day).
public record CalendarEvent(
    CalendarEventKind Kind,
    DateOnly Date,
    TimeOnly? Time,
    ScheduleState State,
    int ApplicationId,
    string CompanyName,
    string Position,
    ApplicationStatus ApplicationStatus,
    int? InterviewId,
    InterviewType? InterviewType,
    InterviewStatus? InterviewStatus,
    string? Location);

// Document metadata. The internal file name is deliberately NOT included.
public record DocumentResponse(
    int Id, int ApplicationId, DocumentCategory Category, string OriginalFileName,
    long SizeBytes, string Sha256, DateTime UploadedAt)
{
    public static DocumentResponse FromEntity(ApplicationDocument d) => new(
        d.Id, d.JobApplicationId, d.Category, d.OriginalFileName, d.SizeBytes, d.Sha256, d.UploadedAt);
}

public record EventResponse(
    int Id, EventType Type, ApplicationStatus? FromStatus, ApplicationStatus? ToStatus,
    string Description, DateTime OccurredAt);

// Short info about a possible duplicate, shown in the warning.
public record DuplicateResponse(int Id, string CompanyName, string Position, ApplicationStatus Status, DateTime CreatedAt);

// ---------- Verlauf (timeline) & waiting queue ----------
// Deliberately compact: no notes, locations, contacts or documents.

// All instants (Start, End, RecordedAt, GeneratedAt) are UTC recording times.
// "…Day" values are days since the submission date (Day 0), null without AppliedOn.
public record TimelineResponse(DateTime GeneratedAt, DateTime? RangeStart, DateTime? RangeEnd, List<JourneyResponse> Applications);

public record JourneyResponse(
    int Id, string CompanyName, string Position, ApplicationStatus CurrentStatus,
    DateOnly? AppliedOn, DateOnly? Deadline, DateTime CreatedAt,
    bool HistoryComplete,      // false: parts of the history were not recorded
    bool StartsBeforeRange,    // the first segment began before the selected range
    double? CurrentDay,        // "today" in days since submission
    int? DeadlineDay,
    List<StatusSegment> Segments,
    List<TransitionMarker> Transitions,
    List<InterviewMarker> Interviews);

// Time in one recorded status. End = null: still the current status.
// Uncertain = the start or the status itself could not be reconstructed exactly.
public record StatusSegment(ApplicationStatus Status, DateTime Start, DateTime? End, bool Uncertain, double? StartDay, double? EndDay);

// One recorded status entry. Initial = the status the application was created with.
public record TransitionMarker(DateTime RecordedAt, ApplicationStatus? From, ApplicationStatus To, bool Backward, bool Initial, double? Day);

// Interview appointment (local wall-clock time, like everywhere else).
public record InterviewMarker(int Id, DateTime ScheduledAt, InterviewType Type, InterviewStatus Status, double? Day);

public record WaitingItem(
    int Id, string CompanyName, string Position, ApplicationStatus Status, DateOnly? AppliedOn,
    int? DaysSinceSubmission,       // null: no submission date entered
    DateTime? LastStatusChangeAt,   // UTC recording time of the last status entry
    int? DaysSinceStatusChange);    // null: no recorded status entry

// ---------- Analyse (historical milestones) ----------
// Compact: no notes, locations, contacts or documents.

public record AnalysisResponse(
    DateOnly? From, DateOnly? To,           // submission period (entered submission dates)
    DateOnly CutoffDate, DateTime Cutoff,   // observation cutoff: evidence recorded until then (UTC)
    int CohortSize,
    int MinCohortForPercent,
    List<MilestoneResult> Milestones,
    OutcomeSummary Outcomes,
    int IncompleteHistoryCount,
    List<AnalysisApplication> Applications, // the cohort – the drill-down filters this list
    WithoutSubmissionDate WithoutSubmissionDate);

// ApplicationIds = exactly the applications counted (Count == ApplicationIds.Count).
// Percent is null for small cohorts (see AnalysisService.MinCohortForPercent).
public record MilestoneResult(Milestone Milestone, int Count, int? Percent, List<int> ApplicationIds);

// Status at the cutoff. Unknown = no status recorded by then.
public record OutcomeSummary(int Open, int Offer, int Rejected, int Unknown);

public record AnalysisApplication(
    int Id, string CompanyName, string Position, ApplicationStatus CurrentStatus,
    ApplicationStatus? StatusAtCutoff, DateOnly? AppliedOn, bool HistoryComplete, List<Evidence> Evidence);

// One piece of evidence. RecordedAt: UTC time it was recorded; Date: entered date (submission);
// AppointmentAt: local interview time.
public record Evidence(
    Milestone Milestone, EvidenceSource Source, DateTime? RecordedAt, DateOnly? Date,
    ApplicationStatus? Status, DateTime? AppointmentAt, InterviewStatus? InterviewStatus);

public record WithoutSubmissionDate(int Drafts, int Other, List<AnalysisApplication> Applications);

// ---------- Dashboard ----------

public record DashboardResponse(
    int Total,
    int Drafts,
    int InProgress,
    int Interviews,
    int Offers,
    int Rejections,
    int? ResponseRatePercent, // null when nothing has been submitted yet
    List<StatusCount> StatusDistribution,
    List<MonthCount> MonthlyActivity,
    List<ActivityItem> RecentActivity);

public record StatusCount(ApplicationStatus Status, int Count, int? Percent);

public record MonthCount(int Year, int Month, int Count);

public record ActivityItem(
    int ApplicationId, string CompanyName, string Position,
    EventType Type, string Description, DateTime OccurredAt);
