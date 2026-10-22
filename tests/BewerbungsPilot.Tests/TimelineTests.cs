using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Tests;

// Reconstruction of the recorded status history (TimelineService).
// Most tests build data in memory and call the pure BuildJourney/ClipToRange functions.
public class TimelineTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _database.Dispose();

    private static DateTime At(int month, int day, int hour = 10) => new(2026, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static JobApplication App(ApplicationStatus current, DateOnly? appliedOn = null, DateTime? statusChangedAt = null) => new()
    {
        Id = 1, CompanyName = "Nordlicht", Position = "Azubi FIAE", Status = current, AppliedOn = appliedOn,
        CreatedAt = At(9, 1), StatusChangedAt = statusChangedAt ?? At(9, 1), Notes = "privat"
    };

    private static int _nextId = 1;
    private static ApplicationEvent Created(ApplicationStatus status, DateTime at) =>
        new() { Id = _nextId++, Type = EventType.Created, ToStatus = status, OccurredAt = at };
    private static ApplicationEvent Changed(ApplicationStatus from, ApplicationStatus to, DateTime at) =>
        new() { Id = _nextId++, Type = EventType.StatusChanged, FromStatus = from, ToStatus = to, OccurredAt = at };

    private static JourneyResponse Build(JobApplication app, params ApplicationEvent[] events) =>
        TimelineService.BuildJourney(app, events, [], Now, Utc);

    // ---------- Reconstruction ----------

    [Fact]
    public void CompleteHistory_GivesOneSegmentPerRecordedStatus_LastOneOpen()
    {
        var journey = Build(App(ApplicationStatus.InPruefung),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 20)), // given out of order on purpose
            Created(ApplicationStatus.Entwurf, At(9, 1)),
            Changed(ApplicationStatus.Entwurf, ApplicationStatus.Beworben, At(9, 5)));

        Assert.True(journey.HistoryComplete);
        Assert.Equal(new[] { ApplicationStatus.Entwurf, ApplicationStatus.Beworben, ApplicationStatus.InPruefung },
            journey.Segments.Select(s => s.Status));
        Assert.Equal((At(9, 1), At(9, 5)), (journey.Segments[0].Start, journey.Segments[0].End!.Value));
        Assert.Equal(At(9, 5), journey.Segments[1].Start);
        Assert.Null(journey.Segments[2].End); // current status: open-ended
        Assert.All(journey.Segments, s => Assert.False(s.Uncertain));
        Assert.True(journey.Transitions[0].Initial);
    }

    [Fact]
    public void SeveralTransitionsAtTheSameTime_KeepTheSavedOrder()
    {
        var sameMoment = At(9, 5);
        var journey = Build(App(ApplicationStatus.InPruefung),
            Created(ApplicationStatus.Entwurf, At(9, 1)),
            Changed(ApplicationStatus.Entwurf, ApplicationStatus.Beworben, sameMoment),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, sameMoment));

        Assert.True(journey.HistoryComplete);
        Assert.Equal(ApplicationStatus.InPruefung, journey.Segments[^1].Status);
        Assert.Equal(TimeSpan.Zero, journey.Segments[1].End!.Value - journey.Segments[1].Start); // zero-length, not dropped
    }

    [Fact]
    public void RepeatedAndBackwardTransitions_AreSeparateSegmentsAndMarkedBackward()
    {
        var journey = Build(App(ApplicationStatus.InPruefung),
            Created(ApplicationStatus.Beworben, At(9, 1)),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 5)),
            Changed(ApplicationStatus.InPruefung, ApplicationStatus.Beworben, At(9, 8)),   // back
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 12)));

        Assert.Equal(4, journey.Segments.Count);
        Assert.Equal(2, journey.Segments.Count(s => s.Status == ApplicationStatus.InPruefung));
        Assert.Equal(new[] { false, false, true, false }, journey.Transitions.Select(t => t.Backward));
    }

    [Fact]
    public void ReopeningAfterRejection_IsBackward()
    {
        Assert.True(TimelineService.IsBackward(ApplicationStatus.Absage, ApplicationStatus.InPruefung));
        Assert.False(TimelineService.IsBackward(ApplicationStatus.Angebot, ApplicationStatus.Absage));
    }

    [Fact]
    public void OnlyOneRecordedStatus_GivesOneOpenSegment()
    {
        var journey = Build(App(ApplicationStatus.Beworben), Created(ApplicationStatus.Beworben, At(9, 1)));

        var segment = Assert.Single(journey.Segments);
        Assert.Null(segment.End);
        Assert.True(journey.HistoryComplete);
    }

    [Fact]
    public void NoRecordedHistory_OnlyCurrentStatus_MarkedUncertain()
    {
        var journey = Build(App(ApplicationStatus.InPruefung, statusChangedAt: At(9, 10)));

        Assert.False(journey.HistoryComplete);
        var segment = Assert.Single(journey.Segments);
        Assert.Equal((ApplicationStatus.InPruefung, At(9, 10), true), (segment.Status, segment.Start, segment.Uncertain));
        Assert.Empty(journey.Transitions); // nothing invented
    }

    [Fact]
    public void MissingCreatedEntry_StatusBeforeFirstChangeIsUncertain()
    {
        var journey = Build(App(ApplicationStatus.InPruefung),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 15)));

        Assert.False(journey.HistoryComplete);
        Assert.Equal(2, journey.Segments.Count);
        Assert.Equal((ApplicationStatus.Beworben, true), (journey.Segments[0].Status, journey.Segments[0].Uncertain));
        Assert.False(journey.Segments[1].Uncertain);
    }

    [Fact]
    public void GapInTheChain_IsReportedAsIncomplete_WithoutInventingStages()
    {
        var journey = Build(App(ApplicationStatus.Angebot),
            Created(ApplicationStatus.Entwurf, At(9, 1)),
            Changed(ApplicationStatus.Vorstellungsgespraech, ApplicationStatus.Angebot, At(9, 20))); // Entwurf → … missing

        Assert.False(journey.HistoryComplete);
        Assert.Equal(new[] { ApplicationStatus.Entwurf, ApplicationStatus.Angebot }, journey.Segments.Select(s => s.Status));
    }

    [Fact]
    public void CurrentStatusDiffersFromLastRecordedOne_AddsUncertainCurrentSegment()
    {
        var journey = Build(App(ApplicationStatus.Absage, statusChangedAt: At(10, 1)),
            Created(ApplicationStatus.Beworben, At(9, 1)));

        Assert.False(journey.HistoryComplete);
        Assert.Equal(At(10, 1), journey.Segments[0].End);
        Assert.Equal((ApplicationStatus.Absage, true, (DateTime?)null),
            (journey.Segments[1].Status, journey.Segments[1].Uncertain, journey.Segments[1].End));
    }

    // ---------- Day 0 alignment ----------

    [Fact]
    public void DaysSinceSubmission_AreMeasuredFromTheEnteredDate()
    {
        var journey = Build(App(ApplicationStatus.InPruefung, appliedOn: new DateOnly(2026, 9, 3)),
            Created(ApplicationStatus.Entwurf, At(9, 1, 12)),                                     // before submission
            Changed(ApplicationStatus.Entwurf, ApplicationStatus.Beworben, At(9, 3, 12)),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 13, 12)));

        Assert.Equal(-1.5, journey.Segments[0].StartDay);
        Assert.Equal(0.5, journey.Segments[1].StartDay);
        Assert.Equal(10.5, journey.Segments[2].StartDay);
        Assert.Equal(new DateTime(2026, 10, 22, 9, 0, 0).Subtract(new DateTime(2026, 9, 3)).TotalDays, journey.CurrentDay);
    }

    [Fact]
    public void WithoutSubmissionDate_NoDayValuesAreInvented()
    {
        var journey = Build(App(ApplicationStatus.Entwurf), Created(ApplicationStatus.Entwurf, At(9, 1)));

        Assert.Null(journey.AppliedOn);
        Assert.Null(journey.CurrentDay);
        Assert.All(journey.Segments, s => Assert.Null(s.StartDay));
    }

    [Fact]
    public void DaysSinceSubmission_UseLocalWallClock_AcrossDaylightSavingChange()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var app = App(ApplicationStatus.InPruefung, appliedOn: new DateOnly(2026, 10, 20));
        // 10:00 Berlin on 30.10. (after the switch on 25.10.) = 09:00 UTC
        var events = new[] { Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc)) };

        var journey = TimelineService.BuildJourney(app, events, [], Now, berlin);

        Assert.Equal(10 + 10 / 24.0, journey.Transitions[0].Day!.Value, precision: 6); // exactly 10 days + 10 hours
    }

    [Fact]
    public void InterviewsAndDeadline_GetDayValues()
    {
        var app = App(ApplicationStatus.Vorstellungsgespraech, appliedOn: new DateOnly(2026, 9, 1));
        app.Deadline = new DateOnly(2026, 8, 25);
        var interview = new Interview { Id = 7, ScheduledAt = new DateTime(2026, 9, 15, 14, 30, 0), Type = InterviewType.Video };

        var journey = TimelineService.BuildJourney(app, [Created(ApplicationStatus.Beworben, At(9, 1))], [interview], Now, Utc);

        Assert.Equal(-7, journey.DeadlineDay);
        Assert.Equal(14 + 14.5 / 24, Assert.Single(journey.Interviews).Day!.Value, precision: 6);
    }

    // ---------- Date range ----------

    [Fact]
    public void Range_KeepsTheStatusActiveAtRangeStart_DropsOlderSegments()
    {
        var journey = Build(App(ApplicationStatus.Vorstellungsgespraech),
            Created(ApplicationStatus.Entwurf, At(9, 1)),
            Changed(ApplicationStatus.Entwurf, ApplicationStatus.Beworben, At(9, 5)),
            Changed(ApplicationStatus.Beworben, ApplicationStatus.InPruefung, At(9, 10)),
            Changed(ApplicationStatus.InPruefung, ApplicationStatus.Vorstellungsgespraech, At(10, 15)));

        var clipped = TimelineService.ClipToRange(journey, At(10, 1, 0), At(10, 23, 0), Now)!;

        Assert.Equal(new[] { ApplicationStatus.InPruefung, ApplicationStatus.Vorstellungsgespraech }, clipped.Segments.Select(s => s.Status));
        Assert.Equal(At(9, 10), clipped.Segments[0].Start); // real start kept: "In Prüfung since 10.09."
        Assert.True(clipped.StartsBeforeRange);
        Assert.Single(clipped.Transitions); // only the change on 15.10. lies inside the range
    }

    [Fact]
    public void Range_EntirelyBeforeTheHistory_ExcludesTheApplication()
    {
        var journey = Build(App(ApplicationStatus.Beworben), Created(ApplicationStatus.Beworben, At(9, 1)));

        Assert.Null(TimelineService.ClipToRange(journey, At(7, 1, 0), At(8, 1, 0), Now));
    }

    [Fact]
    public void Range_OpenCurrentStatus_ReachesIntoLaterRange()
    {
        var journey = Build(App(ApplicationStatus.Beworben), Created(ApplicationStatus.Beworben, At(9, 1)));

        var clipped = TimelineService.ClipToRange(journey, At(10, 1, 0), At(10, 23, 0), Now);

        Assert.NotNull(clipped);
        Assert.True(clipped!.StartsBeforeRange);
    }

    // ---------- Through the database ----------

    [Fact]
    public async Task Service_ReconstructsFromRealHistory_AndHidesPrivateFields()
    {
        var clock = new FixedClock(new DateTimeOffset(Now));
        int id;
        using (var db = _database.CreateContext())
        {
            var service = new ApplicationService(db, clock, _database.Storage);
            id = (await service.CreateAsync(new ApplicationInput("Nordlicht", "Azubi", "Hamburg", null, null,
                new DateOnly(2026, 10, 1), ApplicationStatus.Beworben, "Geheime Notiz"))).Id;
            await service.ChangeStatusAsync(id, ApplicationStatus.InPruefung);
        }

        using var readDb = _database.CreateContext();
        var timeline = await new TimelineService(readDb, clock).GetTimelineAsync();

        var journey = Assert.Single(timeline.Applications);
        Assert.Equal(id, journey.Id);
        Assert.Equal(2, journey.Segments.Count);
        Assert.True(journey.HistoryComplete);
        Assert.DoesNotContain(typeof(JourneyResponse).GetProperties(), p => p.Name is "Notes" or "Location" or "JobUrl");
    }
}
