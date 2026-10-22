namespace BewerbungsPilot.Data;

// The stages an application moves through. Stored as text in the database
// (e.g. "InPruefung"), so the database stays readable.
public enum ApplicationStatus
{
    Entwurf,
    Beworben,
    InPruefung,
    Vorstellungsgespraech,
    Angebot,
    Absage
}

public enum EventType
{
    Created,
    StatusChanged,
    InterviewScheduled,
    DocumentAdded
}

// One job application = one row in the "Applications" table.
public class JobApplication
{
    public int Id { get; set; }
    public string CompanyName { get; set; } = "";
    public string Position { get; set; } = "";
    public string? Location { get; set; }
    public string? JobUrl { get; set; }
    public DateOnly? Deadline { get; set; }
    public DateOnly? AppliedOn { get; set; }
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Entwurf;
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime StatusChangedAt { get; set; }

    // Navigation property: all history entries belonging to this application (1:n).
    public List<ApplicationEvent> Events { get; set; } = new();
}

// One entry in the history ("Verlauf") of an application.
public class ApplicationEvent
{
    public int Id { get; set; }
    public int JobApplicationId { get; set; }          // foreign key
    public JobApplication? JobApplication { get; set; } // navigation property

    public EventType Type { get; set; }
    public ApplicationStatus? FromStatus { get; set; }
    public ApplicationStatus? ToStatus { get; set; }
    public string Description { get; set; } = "";
    public DateTime OccurredAt { get; set; }
}

public static class StatusLabels
{
    // German display names, used for history descriptions.
    public static readonly Dictionary<ApplicationStatus, string> German = new()
    {
        [ApplicationStatus.Entwurf] = "Entwurf",
        [ApplicationStatus.Beworben] = "Beworben",
        [ApplicationStatus.InPruefung] = "In Prüfung",
        [ApplicationStatus.Vorstellungsgespraech] = "Vorstellungsgespräch",
        [ApplicationStatus.Angebot] = "Angebot",
        [ApplicationStatus.Absage] = "Absage"
    };
}
