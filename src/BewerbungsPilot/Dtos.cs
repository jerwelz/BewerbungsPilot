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
