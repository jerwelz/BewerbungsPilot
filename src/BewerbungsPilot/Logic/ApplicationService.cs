using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// All business rules for job applications live here.
// Endpoints call this class; this class talks to the database.
public class ApplicationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly DocumentStorage _storage;

    public ApplicationService(AppDbContext db, TimeProvider clock, DocumentStorage storage)
    {
        _db = db;
        _clock = clock;
        _storage = storage;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    public DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    // Returns field name → error messages. An empty dictionary means "valid".
    public const int MaxNotesLength = 10_000;

    // Used for both creating and editing.
    public Dictionary<string, string[]> Validate(ApplicationInput input)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(input.CompanyName))
            errors["companyName"] = ["Bitte geben Sie das Unternehmen an."];
        else if (input.CompanyName.Trim().Length > 200)
            errors["companyName"] = ["Der Unternehmensname darf höchstens 200 Zeichen lang sein."];

        if (string.IsNullOrWhiteSpace(input.Position))
            errors["position"] = ["Bitte geben Sie die Position an."];
        else if (input.Position.Trim().Length > 200)
            errors["position"] = ["Die Position darf höchstens 200 Zeichen lang sein."];

        if (input.Location?.Trim().Length > 200)
            errors["location"] = ["Der Ort darf höchstens 200 Zeichen lang sein."];

        if (input.JobUrl?.Trim().Length > 2000)
            errors["jobUrl"] = ["Die Web-Adresse darf höchstens 2000 Zeichen lang sein."];
        else if (!string.IsNullOrWhiteSpace(input.JobUrl) && !IsWebAddress(input.JobUrl.Trim()))
            errors["jobUrl"] = ["Bitte geben Sie eine gültige Web-Adresse an (beginnend mit http:// oder https://)."];

        if (input.Notes?.Trim().Length > MaxNotesLength)
            errors["notes"] = [$"Die Notizen dürfen höchstens {MaxNotesLength} Zeichen lang sein."];

        if (input.AppliedOn > Today)
            errors["appliedOn"] = ["Das Bewerbungsdatum darf nicht in der Zukunft liegen."];

        if (!Enum.IsDefined(input.Status))
            errors["status"] = ["Unbekannter Status."];

        return errors;
    }

    public async Task<List<JobApplication>> GetAllAsync()
    {
        return await _db.Applications
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();
    }

    // Loads one application including its history (oldest first), documents (newest first)
    // and interviews (in date order).
    public async Task<JobApplication?> GetByIdAsync(int id)
    {
        return await _db.Applications
            .Include(a => a.Events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id))
            .Include(a => a.Documents.OrderByDescending(d => d.UploadedAt))
            .Include(a => a.Interviews.OrderBy(i => i.ScheduledAt))
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    // Creates the application and its first history entry.
    // Both are written in ONE SaveChangesAsync call, which EF Core runs
    // inside a single database transaction: either both are saved or neither.
    public async Task<JobApplication> CreateAsync(ApplicationInput input)
    {
        var now = UtcNow;
        var application = new JobApplication
        {
            CreatedAt = now,
            StatusChangedAt = now,
            Status = input.Status
        };
        CopyInput(input, application, now);
        EnsureAppliedOn(application);

        application.Events.Add(new ApplicationEvent
        {
            Type = EventType.Created,
            ToStatus = application.Status,
            Description = $"Bewerbung angelegt (Status: {StatusLabels.German[application.Status]})",
            OccurredAt = now
        });

        _db.Applications.Add(application);
        await _db.SaveChangesAsync();
        return application;
    }

    // Updates an application. Returns null if the id does not exist.
    // A history entry is added only if the status really changed.
    // Again ONE SaveChangesAsync → application and history entry are saved together.
    public async Task<JobApplication?> UpdateAsync(int id, ApplicationInput input)
    {
        var application = await _db.Applications.FindAsync(id);
        if (application is null)
            return null;

        var now = UtcNow;
        var oldDeadline = application.Deadline;
        CopyInput(input, application, now);
        SetStatus(application, input.Status, now);
        if (application.Deadline != oldDeadline)
            application.Events.Add(DeadlineChangedEvent(oldDeadline, application.Deadline, now));

        await _db.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    // Changes ONLY the status (used by the Kanban board). All other fields stay as they are.
    // Returns null if the id does not exist. Uses the same SetStatus rule as UpdateAsync.
    public async Task<JobApplication?> ChangeStatusAsync(int id, ApplicationStatus newStatus)
    {
        var application = await _db.Applications.FindAsync(id);
        if (application is null)
            return null;

        var now = UtcNow;
        if (SetStatus(application, newStatus, now))
            application.UpdatedAt = now;

        await _db.SaveChangesAsync(); // status + history entry in one transaction
        return application;
    }

    // THE status-change rule – the only place where a status is changed.
    // Adds exactly one history entry if the status really changes.
    // Returns true if something changed.
    private bool SetStatus(JobApplication application, ApplicationStatus newStatus, DateTime now)
    {
        var oldStatus = application.Status;
        application.Status = newStatus;
        var appliedOnSet = EnsureAppliedOn(application);

        if (newStatus == oldStatus)
            return appliedOnSet;

        application.StatusChangedAt = now;
        application.Events.Add(new ApplicationEvent
        {
            Type = EventType.StatusChanged,
            FromStatus = oldStatus,
            ToStatus = newStatus,
            Description = $"Status geändert: {StatusLabels.German[oldStatus]} → {StatusLabels.German[newStatus]}",
            OccurredAt = now
        });
        return true;
    }

    private static ApplicationEvent DeadlineChangedEvent(DateOnly? oldDeadline, DateOnly? newDeadline, DateTime now) => new()
    {
        Type = EventType.DeadlineChanged,
        Description = (oldDeadline, newDeadline) switch
        {
            (null, { } added) => $"Bewerbungsfrist festgelegt: {ScheduleRules.FormatDate(added)}",
            ({ } removed, null) => $"Bewerbungsfrist entfernt (war {ScheduleRules.FormatDate(removed)})",
            ({ } from, { } to) => $"Bewerbungsfrist geändert: {ScheduleRules.FormatDate(from)} → {ScheduleRules.FormatDate(to)}",
            _ => "Bewerbungsfrist geändert"
        },
        OccurredAt = now
    };

    // Rule: anything beyond "Entwurf" has been submitted, so it needs a submission date.
    // Returns true if the date was filled in.
    private bool EnsureAppliedOn(JobApplication application)
    {
        if (application.Status == ApplicationStatus.Entwurf || application.AppliedOn is not null)
            return false;
        application.AppliedOn = Today;
        return true;
    }

    // Deletes an application. Its history and document rows are removed by the database
    // (cascade delete, configured in AppDbContext); the document files are deleted here.
    // Returns false if not found.
    public async Task<bool> DeleteAsync(int id)
    {
        var application = await _db.Applications
            .Include(a => a.Documents)
            .FirstOrDefaultAsync(a => a.Id == id);
        if (application is null)
            return false;

        var files = application.Documents.Select(d => d.StoredFileName).ToList();
        _db.Applications.Remove(application);
        await _db.SaveChangesAsync();

        // Files only after the database change succeeded (same reasoning as DocumentService.DeleteAsync).
        foreach (var file in files)
            _storage.TryDelete(file);
        return true;
    }

    // Finds existing applications with the same company AND position
    // (ignoring upper/lower case and extra spaces). When editing, the
    // application itself is excluded via excludeId.
    public async Task<List<JobApplication>> FindDuplicatesAsync(string? companyName, string? position, int? excludeId)
    {
        var company = Normalize(companyName);
        var job = Normalize(position);
        if (company == "" || job == "")
            return new List<JobApplication>();

        // Compared in C# instead of SQL: SQLite's lower() does not handle umlauts (Ä/ä).
        // For a personal application list this is fast enough.
        var all = await _db.Applications.ToListAsync();
        return all
            .Where(a => a.Id != excludeId)
            .Where(a => Normalize(a.CompanyName) == company && Normalize(a.Position) == job)
            .ToList();
    }

    // "  Nordlicht   Software GmbH " → "nordlicht software gmbh"
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', words).ToLowerInvariant();
    }

    // Shared by create and update: copies the form values onto the entity.
    // The status is NOT copied here – it is set via SetStatus so the history stays correct.
    private static void CopyInput(ApplicationInput input, JobApplication application, DateTime now)
    {
        application.CompanyName = input.CompanyName!.Trim();
        application.Position = input.Position!.Trim();
        application.Location = Clean(input.Location);
        application.JobUrl = Clean(input.JobUrl);
        application.Deadline = input.Deadline;
        application.AppliedOn = input.AppliedOn;
        application.Notes = Clean(input.Notes);
        application.UpdatedAt = now;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
