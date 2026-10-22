using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// All business rules for job applications live here.
// Endpoints call this class; this class talks to the database.
public class ApplicationService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ApplicationService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    // Returns field name → error messages. An empty dictionary means "valid".
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

        if (!string.IsNullOrWhiteSpace(input.JobUrl) && !IsWebAddress(input.JobUrl.Trim()))
            errors["jobUrl"] = ["Bitte geben Sie eine gültige Web-Adresse an (beginnend mit http:// oder https://)."];

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

    // Loads one application including its history, oldest entry first.
    public async Task<JobApplication?> GetByIdAsync(int id)
    {
        return await _db.Applications
            .Include(a => a.Events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id))
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
            StatusChangedAt = now
        };
        CopyInput(input, application, now);

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
        var oldStatus = application.Status;
        CopyInput(input, application, now);

        if (application.Status != oldStatus)
        {
            application.StatusChangedAt = now;
            application.Events.Add(new ApplicationEvent
            {
                Type = EventType.StatusChanged,
                FromStatus = oldStatus,
                ToStatus = application.Status,
                Description = $"Status geändert: {StatusLabels.German[oldStatus]} → {StatusLabels.German[application.Status]}",
                OccurredAt = now
            });
        }

        await _db.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    // Deletes an application. Its history is removed by the database
    // (cascade delete, configured in AppDbContext). Returns false if not found.
    public async Task<bool> DeleteAsync(int id)
    {
        var application = await _db.Applications.FindAsync(id);
        if (application is null)
            return false;

        _db.Applications.Remove(application);
        await _db.SaveChangesAsync();
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
    private void CopyInput(ApplicationInput input, JobApplication application, DateTime now)
    {
        application.CompanyName = input.CompanyName!.Trim();
        application.Position = input.Position!.Trim();
        application.Location = Clean(input.Location);
        application.JobUrl = Clean(input.JobUrl);
        application.Deadline = input.Deadline;
        application.AppliedOn = input.AppliedOn;
        application.Status = input.Status;
        application.Notes = Clean(input.Notes);
        application.UpdatedAt = now;

        // Rule: anything beyond "Entwurf" has been submitted, so it needs a submission date.
        if (application.Status != ApplicationStatus.Entwurf && application.AppliedOn is null)
            application.AppliedOn = Today;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
