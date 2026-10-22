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
    DocumentAdded,
    DocumentDeleted,
    InterviewRescheduled,
    InterviewCompleted,
    InterviewCancelled,
    InterviewDeleted,
    DeadlineChanged
}

// How precise the confirmed map position is. Chosen by the user, never guessed.
public enum LocationPrecision
{
    Unknown,     // no confirmed position (default)
    Exact,       // confirmed workplace address
    Approximate, // e.g. street or district
    City,        // only the town/city
    Remote       // remote position – deliberately no map position
}

public enum InterviewType
{
    Telefon,
    Video,
    VorOrt
}

public enum InterviewStatus
{
    Geplant,
    Abgeschlossen,
    Abgesagt
}

public enum DocumentCategory
{
    Lebenslauf,
    Anschreiben,
    Zeugnis,
    Stellenanzeige,
    Sonstiges
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

    // Confirmed map position (page "Standorte"). Only set when the user confirms it;
    // Location above stays the free text the user typed.
    public LocationPrecision LocationPrecision { get; set; } = LocationPrecision.Unknown;
    public double? MapLatitude { get; set; }
    public double? MapLongitude { get; set; }
    public string? MapLabel { get; set; } // e.g. "Hafenstraße 12, Hamburg" or "Hamburg"

    // Navigation property: all history entries belonging to this application (1:n).
    public List<ApplicationEvent> Events { get; set; } = new();

    // Navigation property: all attached documents (1:n).
    public List<ApplicationDocument> Documents { get; set; } = new();

    // Navigation property: all interviews (1:n).
    public List<Interview> Interviews { get; set; } = new();
}

// One interview appointment for an application.
public class Interview
{
    public int Id { get; set; }
    public int JobApplicationId { get; set; }          // foreign key
    public JobApplication? JobApplication { get; set; } // navigation property

    // Local wall-clock time exactly as the user entered it (German time), WITHOUT
    // time zone. "14:30" stays 14:30 – also across daylight saving changes.
    public DateTime ScheduledAt { get; set; }
    public InterviewType Type { get; set; }
    public InterviewStatus Status { get; set; } = InterviewStatus.Geplant;
    public string? Location { get; set; }      // address or meeting link
    public string? ContactPerson { get; set; }
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }    // UTC, like all system timestamps
    public DateTime UpdatedAt { get; set; }
}

// Metadata of one uploaded file. The file itself lives in storage/documents/
// under StoredFileName – never under the original name, so two uploads called
// "Lebenslauf.pdf" can never overwrite each other.
public class ApplicationDocument
{
    public int Id { get; set; }
    public int JobApplicationId { get; set; }          // foreign key
    public JobApplication? JobApplication { get; set; } // navigation property

    public DocumentCategory Category { get; set; }
    public string OriginalFileName { get; set; } = ""; // shown to the user, used for downloads
    public string StoredFileName { get; set; } = "";   // e.g. "3f2a…9c.pdf" (random GUID)
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";           // fingerprint: proves the file is unchanged
    public DateTime UploadedAt { get; set; }
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

    public static readonly Dictionary<InterviewType, string> InterviewTypes = new()
    {
        [InterviewType.Telefon] = "Telefon",
        [InterviewType.Video] = "Video",
        [InterviewType.VorOrt] = "Vor Ort"
    };
}
