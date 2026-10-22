using System.Globalization;
using BewerbungsPilot.Data;

namespace BewerbungsPilot.Logic;

// Calculated (not stored) state of a deadline or interview.
public enum ScheduleState
{
    Open,     // deadline: still time / interview: planned, not yet past
    DueSoon,  // deadline: due within the next DeadlineWarningDays days
    Overdue,  // deadline: missed / interview: day is over but still "Geplant"
    Done,     // deadline: application was submitted / interview: "Abgeschlossen"
    Cancelled // interview: "Abgesagt"
}

// The exact rules for deadlines and interviews – in one place, so they can be tested.
//
// Date and time conventions of the whole application:
// - Deadlines are calendar days (DateOnly), valid until the end of that day.
// - Interview times are local wall-clock times as entered (no time zone).
// - "Today" and "now" are the local date/time of the computer running the app.
// - System timestamps (CreatedAt, history, uploads) are stored in UTC.
public static class ScheduleRules
{
    public const int DeadlineWarningDays = 7;

    // Deadline rules:
    // - No deadline → null.
    // - Only applications in "Entwurf" still need their deadline. Once submitted
    //   (any other status, including Absage) the deadline is Done – never overdue.
    // - Overdue: the deadline day is before today (the deadline day itself is still OK).
    // - DueSoon: today … today + 7 days.
    public static ScheduleState? DeadlineState(ApplicationStatus status, DateOnly? deadline, DateOnly today)
    {
        if (deadline is null)
            return null;
        if (status != ApplicationStatus.Entwurf)
            return ScheduleState.Done;
        if (deadline < today)
            return ScheduleState.Overdue;
        if (deadline <= today.AddDays(DeadlineWarningDays))
            return ScheduleState.DueSoon;
        return ScheduleState.Open;
    }

    // Interview rules:
    // - Abgeschlossen → Done, Abgesagt → Cancelled.
    // - Geplant and the interview DAY is over → Overdue ("please record the result").
    //   An interview earlier today stays Open until midnight.
    public static ScheduleState InterviewState(InterviewStatus status, DateTime scheduledAt, DateOnly today)
    {
        return status switch
        {
            InterviewStatus.Abgeschlossen => ScheduleState.Done,
            InterviewStatus.Abgesagt => ScheduleState.Cancelled,
            _ when DateOnly.FromDateTime(scheduledAt) < today => ScheduleState.Overdue,
            _ => ScheduleState.Open
        };
    }

    // German formats for history texts, independent of the server's language settings.
    public static string FormatDate(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    public static string FormatDateTime(DateTime dateTime) =>
        dateTime.ToString("dd.MM.yyyy, HH:mm", CultureInfo.InvariantCulture) + " Uhr";
}
