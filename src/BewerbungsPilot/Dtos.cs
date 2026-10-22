using BewerbungsPilot.Data;

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
    DateTime StatusChangedAt)
{
    public static ApplicationResponse FromEntity(JobApplication a) => new(
        a.Id, a.CompanyName, a.Position, a.Location, a.JobUrl, a.Deadline, a.AppliedOn,
        a.Status, a.Notes, a.CreatedAt, a.UpdatedAt, a.StatusChangedAt);
}
