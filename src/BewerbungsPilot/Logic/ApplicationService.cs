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

    public async Task<JobApplication?> GetByIdAsync(int id)
    {
        return await _db.Applications.FindAsync(id);
    }

    // Creates the application and its first history entry.
    // Both are written in ONE SaveChangesAsync call, which EF Core runs
    // inside a single database transaction: either both are saved or neither.
    public async Task<JobApplication> CreateAsync(ApplicationInput input)
    {
        var now = UtcNow;
        var application = new JobApplication
        {
            CompanyName = input.CompanyName!.Trim(),
            Position = input.Position!.Trim(),
            Location = Clean(input.Location),
            JobUrl = Clean(input.JobUrl),
            Deadline = input.Deadline,
            AppliedOn = input.AppliedOn,
            Status = input.Status,
            Notes = Clean(input.Notes),
            CreatedAt = now,
            UpdatedAt = now,
            StatusChangedAt = now
        };

        // Rule: anything beyond "Entwurf" has been submitted, so it needs a submission date.
        if (application.Status != ApplicationStatus.Entwurf && application.AppliedOn is null)
            application.AppliedOn = Today;

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

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsWebAddress(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
