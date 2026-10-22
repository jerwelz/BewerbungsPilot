using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Business rules for interviews. Creating an interview does NOT change the
// application's status – the user decides that separately.
public class InterviewService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public InterviewService(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    private DateTime LocalNow => _clock.GetLocalNow().DateTime;
    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    // Returns field name → error messages. `existing` is null when creating.
    public Dictionary<string, string[]> Validate(InterviewInput input, Interview? existing = null)
    {
        var errors = new Dictionary<string, string[]>();

        if (input.ScheduledAt is not { } scheduledAt)
            errors["scheduledAt"] = ["Bitte geben Sie Datum und Uhrzeit an."];
        else if (scheduledAt.Year < 2000 || scheduledAt.Year > 2100)
            errors["scheduledAt"] = ["Bitte geben Sie ein gültiges Datum an."];
        // A *planned* interview in the past is almost always a typo. Past interviews
        // are recorded with the status "Abgeschlossen". Only checked when the date is
        // new or changed, so an overdue interview can still be edited.
        else if (input.Status == InterviewStatus.Geplant && scheduledAt < LocalNow
                 && (existing is null || existing.ScheduledAt != ToMinute(scheduledAt)))
            errors["scheduledAt"] = ["Ein geplantes Gespräch kann nicht in der Vergangenheit liegen. " +
                                     "Für ein vergangenes Gespräch wählen Sie den Status „Abgeschlossen“."];

        if (!Enum.IsDefined(input.Type))
            errors["type"] = ["Bitte wählen Sie die Art des Gesprächs aus."];
        if (!Enum.IsDefined(input.Status))
            errors["status"] = ["Unbekannter Status."];
        if (input.Location?.Trim().Length > 500)
            errors["location"] = ["Ort oder Link darf höchstens 500 Zeichen lang sein."];
        if (input.ContactPerson?.Trim().Length > 200)
            errors["contactPerson"] = ["Die Ansprechperson darf höchstens 200 Zeichen lang sein."];
        if (input.Notes?.Trim().Length > 4000)
            errors["notes"] = ["Die Notizen dürfen höchstens 4000 Zeichen lang sein."];

        return errors;
    }

    public async Task<Interview?> GetAsync(int id) => await _db.Interviews.FindAsync(id);

    // Returns null if the application does not exist.
    public async Task<Interview?> CreateAsync(int applicationId, InterviewInput input)
    {
        if (await _db.Applications.FindAsync(applicationId) is null)
            return null;

        var now = UtcNow;
        var interview = new Interview { JobApplicationId = applicationId, CreatedAt = now };
        CopyInput(input, interview, now);
        _db.Interviews.Add(interview);

        var what = $"Vorstellungsgespräch ({StatusLabels.InterviewTypes[interview.Type]}) am {ScheduleRules.FormatDateTime(interview.ScheduledAt)}";
        AddEvent(applicationId, EventType.InterviewScheduled, interview.Status switch
        {
            InterviewStatus.Abgeschlossen => $"{what} erfasst (abgeschlossen)",
            InterviewStatus.Abgesagt => $"{what} erfasst (abgesagt)",
            _ => $"{what} geplant"
        }, now);

        await _db.SaveChangesAsync(); // interview + history entry in one transaction
        return interview;
    }

    // Returns null if the interview does not exist.
    // History entries only for meaningful changes: new date/time and new status.
    public async Task<Interview?> UpdateAsync(int id, InterviewInput input)
    {
        var interview = await _db.Interviews.FindAsync(id);
        if (interview is null)
            return null;

        var now = UtcNow;
        var oldScheduledAt = interview.ScheduledAt;
        var oldStatus = interview.Status;
        CopyInput(input, interview, now);

        if (interview.ScheduledAt != oldScheduledAt)
            AddEvent(interview.JobApplicationId, EventType.InterviewRescheduled,
                $"Vorstellungsgespräch verschoben: {ScheduleRules.FormatDateTime(oldScheduledAt)} → {ScheduleRules.FormatDateTime(interview.ScheduledAt)}", now);

        if (interview.Status != oldStatus)
        {
            var (type, text) = interview.Status switch
            {
                InterviewStatus.Abgeschlossen => (EventType.InterviewCompleted, "Vorstellungsgespräch abgeschlossen"),
                InterviewStatus.Abgesagt => (EventType.InterviewCancelled, "Vorstellungsgespräch abgesagt"),
                _ => (EventType.InterviewScheduled, "Vorstellungsgespräch wieder geplant")
            };
            AddEvent(interview.JobApplicationId, type, $"{text} ({ScheduleRules.FormatDateTime(interview.ScheduledAt)})", now);
        }

        await _db.SaveChangesAsync();
        return interview;
    }

    // Returns false if the interview does not exist.
    public async Task<bool> DeleteAsync(int id)
    {
        var interview = await _db.Interviews.FindAsync(id);
        if (interview is null)
            return false;

        _db.Interviews.Remove(interview);
        AddEvent(interview.JobApplicationId, EventType.InterviewDeleted,
            $"Vorstellungsgespräch gelöscht ({ScheduleRules.FormatDateTime(interview.ScheduledAt)})", UtcNow);
        await _db.SaveChangesAsync();
        return true;
    }

    private void AddEvent(int applicationId, EventType type, string description, DateTime now) =>
        _db.ApplicationEvents.Add(new ApplicationEvent
        {
            JobApplicationId = applicationId,
            Type = type,
            Description = description,
            OccurredAt = now
        });

    // Seconds are not meaningful for appointments; dropping them keeps comparisons exact.
    private static DateTime ToMinute(DateTime at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, DateTimeKind.Unspecified);

    private static void CopyInput(InterviewInput input, Interview interview, DateTime now)
    {
        interview.ScheduledAt = ToMinute(input.ScheduledAt!.Value);
        interview.Type = input.Type;
        interview.Status = input.Status;
        interview.Location = Clean(input.Location);
        interview.ContactPerson = Clean(input.ContactPerson);
        interview.Notes = Clean(input.Notes);
        interview.UpdatedAt = now;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
