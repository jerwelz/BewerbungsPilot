using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Tests;

// Historical milestone analysis. Data is built in memory and passed to the pure Analyze function.
// "Now": 22.10.2026, 09:00 UTC; time zone UTC.
public class AnalysisTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private static readonly DateTime Now = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 10, 22);

    private readonly List<JobApplication> _applications = [];
    private readonly List<ApplicationEvent> _events = [];
    private readonly List<Interview> _interviews = [];
    private int _nextId = 1;

    public void Dispose() => _database.Dispose();

    private static DateTime At(int month, int day) => new(2026, month, day, 10, 0, 0, DateTimeKind.Utc);

    // Creates an application with a recorded history: (status, recorded at) – the first entry is "Created".
    private JobApplication Add(string company, DateOnly? appliedOn, params (ApplicationStatus Status, DateTime At)[] history)
    {
        var app = new JobApplication
        {
            Id = _applications.Count + 1, CompanyName = company, Position = "Azubi FIAE", AppliedOn = appliedOn,
            Status = history[^1].Status, CreatedAt = history[0].At, StatusChangedAt = history[^1].At
        };
        _applications.Add(app);
        for (var i = 0; i < history.Length; i++)
        {
            _events.Add(new ApplicationEvent
            {
                Id = _nextId++, JobApplicationId = app.Id, OccurredAt = history[i].At, ToStatus = history[i].Status,
                Type = i == 0 ? EventType.Created : EventType.StatusChanged,
                FromStatus = i == 0 ? null : history[i - 1].Status
            });
        }
        return app;
    }

    private void AddInterview(JobApplication app, DateTime recordedAt, InterviewStatus status = InterviewStatus.Abgeschlossen) =>
        _interviews.Add(new Interview
        {
            Id = _interviews.Count + 1, JobApplicationId = app.Id, CreatedAt = recordedAt,
            ScheduledAt = DateTime.SpecifyKind(recordedAt.AddDays(3), DateTimeKind.Unspecified), Type = InterviewType.Video, Status = status
        });

    private AnalysisResponse Analyze(DateOnly? from = null, DateOnly? to = null, DateOnly? cutoff = null) =>
        AnalysisService.Analyze(_applications, _events.ToLookup(e => e.JobApplicationId), _interviews.ToLookup(i => i.JobApplicationId),
            from, to, cutoff ?? Today, Now, TimeZoneInfo.Utc);

    private static MilestoneResult M(AnalysisResponse r, Milestone m) => r.Milestones.Single(x => x.Milestone == m);

    // ---------- Counting rules ----------

    [Fact]
    public void MultipleInterviews_CountOneApplication()
    {
        var app = Add("Nordlicht", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(9, 1)));
        AddInterview(app, At(9, 10));
        AddInterview(app, At(9, 20));
        AddInterview(app, At(9, 25), InterviewStatus.Geplant);

        var interview = M(Analyze(), Milestone.Interview);

        Assert.Equal(1, interview.Count);
        Assert.Equal(new[] { app.Id }, interview.ApplicationIds);
    }

    [Fact]
    public void RejectionAfterInterview_StillCountsAsInterviewReached()
    {
        Add("Hanse", new DateOnly(2026, 9, 1),
            (ApplicationStatus.Beworben, At(9, 1)), (ApplicationStatus.Vorstellungsgespraech, At(9, 10)), (ApplicationStatus.Absage, At(9, 20)));

        var result = Analyze();

        Assert.Equal(1, M(result, Milestone.Interview).Count);
        Assert.Equal(1, result.Outcomes.Rejected);
        Assert.Equal(0, result.Outcomes.Open);
    }

    [Fact]
    public void OfferWithoutRecordedInterview_GetsNoInventedInterview()
    {
        var app = Add("Direkt AG", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(9, 1)), (ApplicationStatus.Angebot, At(9, 15)));

        var result = Analyze();

        Assert.Equal(1, M(result, Milestone.Offer).Count);
        Assert.Equal(0, M(result, Milestone.Interview).Count);
        Assert.DoesNotContain(result.Applications.Single().Evidence, e => e.Milestone == Milestone.Interview);
        Assert.Equal(app.Id, Assert.Single(M(result, Milestone.Offer).ApplicationIds));
    }

    [Fact]
    public void OfferLaterDeclined_StillCountsAsOfferReceived()
    {
        Add("Kiel", new DateOnly(2026, 9, 1),
            (ApplicationStatus.Beworben, At(9, 1)), (ApplicationStatus.Angebot, At(9, 15)), (ApplicationStatus.Absage, At(9, 20)));

        Assert.Equal(1, M(Analyze(), Milestone.Offer).Count);
    }

    [Fact]
    public void CancelledInterviewOnly_IsNoEvidence()
    {
        var app = Add("Abgesagt GmbH", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(9, 1)));
        AddInterview(app, At(9, 10), InterviewStatus.Abgesagt);

        Assert.Equal(0, M(Analyze(), Milestone.Interview).Count);
    }

    [Fact]
    public void MissingHistory_CurrentStatusIsUsedAsRecordedEvidence_AndFlaggedIncomplete()
    {
        // Legacy data: no history entries at all, current status "Vorstellungsgespräch" recorded on 01.10.
        _applications.Add(new JobApplication
        {
            Id = 99, CompanyName = "Altdaten", Position = "Azubi", AppliedOn = new DateOnly(2026, 9, 1),
            Status = ApplicationStatus.Vorstellungsgespraech, CreatedAt = At(9, 1), StatusChangedAt = At(10, 1)
        });

        var result = Analyze();

        Assert.Equal(1, result.IncompleteHistoryCount);
        var evidence = Assert.Single(result.Applications.Single().Evidence, e => e.Milestone == Milestone.Interview);
        Assert.Equal((EvidenceSource.CurrentStatus, (DateTime?)At(10, 1)), (evidence.Source, evidence.RecordedAt));
        Assert.Equal(0, M(result, Milestone.Offer).Count); // nothing more is assumed
    }

    [Fact]
    public void IncompleteHistory_MissingStepsAreNotFilledIn()
    {
        // History jumps from Beworben straight to Angebot ("from" Vorstellungsgespräch was never recorded).
        var app = Add("Lücke KG", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(9, 1)));
        _events.Add(new ApplicationEvent { Id = _nextId++, JobApplicationId = app.Id, Type = EventType.StatusChanged,
            FromStatus = ApplicationStatus.Vorstellungsgespraech, ToStatus = ApplicationStatus.Angebot, OccurredAt = At(9, 20) });
        app.Status = ApplicationStatus.Angebot;

        var result = Analyze();

        Assert.Equal(1, result.IncompleteHistoryCount);
        Assert.Equal(1, M(result, Milestone.Offer).Count);
        Assert.Equal(0, M(result, Milestone.Interview).Count); // "from" value alone is not evidence
    }

    // ---------- Cohort & dates ----------

    [Fact]
    public void SubmissionPeriod_FiltersByEnteredSubmissionDate_NotByCreation()
    {
        Add("September", new DateOnly(2026, 9, 15), (ApplicationStatus.Beworben, At(10, 20))); // entered later, submitted in Sept.
        Add("Oktober", new DateOnly(2026, 10, 5), (ApplicationStatus.Beworben, At(10, 5)));

        var result = Analyze(from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30));

        Assert.Equal("September", Assert.Single(result.Applications).CompanyName);
        Assert.Equal(1, result.CohortSize);
    }

    [Fact]
    public void MissingSubmissionDate_IsReportedSeparately_NeverInCohort()
    {
        Add("Entwurf", null, (ApplicationStatus.Entwurf, At(9, 1)));
        Add("Ohne Datum", null, (ApplicationStatus.InPruefung, At(9, 2)));
        Add("Mit Datum", new DateOnly(2026, 9, 3), (ApplicationStatus.Beworben, At(9, 3)));

        var result = Analyze();

        Assert.Equal(1, result.CohortSize);
        Assert.Equal((1, 1), (result.WithoutSubmissionDate.Drafts, result.WithoutSubmissionDate.Other));
        Assert.Equal(1, M(result, Milestone.Submitted).Count);
    }

    [Fact]
    public void ObservationCutoff_IgnoresEvidenceRecordedLater()
    {
        var app = Add("Nordlicht", new DateOnly(2026, 9, 1),
            (ApplicationStatus.Beworben, At(9, 1)), (ApplicationStatus.Vorstellungsgespraech, At(10, 10)), (ApplicationStatus.Angebot, At(10, 20)));
        AddInterview(app, At(10, 5));

        var atOct7 = Analyze(cutoff: new DateOnly(2026, 10, 7));
        var atOct15 = Analyze(cutoff: new DateOnly(2026, 10, 15));
        var atSep30 = Analyze(cutoff: new DateOnly(2026, 9, 30));

        Assert.Equal((1, 0), (M(atOct7, Milestone.Interview).Count, M(atOct7, Milestone.Offer).Count)); // only the interview entry
        Assert.Equal(ApplicationStatus.Beworben, atOct7.Applications.Single().StatusAtCutoff);
        Assert.Equal((1, 0), (M(atOct15, Milestone.Interview).Count, M(atOct15, Milestone.Offer).Count));
        Assert.Equal(0, M(atSep30, Milestone.Interview).Count);
        Assert.Equal(1, atSep30.Outcomes.Open);
    }

    [Fact]
    public void ObservationCutoff_ExcludesApplicationsSubmittedAfterwards()
    {
        Add("Später", new DateOnly(2026, 10, 10), (ApplicationStatus.Beworben, At(10, 10)));

        Assert.Equal(0, Analyze(cutoff: new DateOnly(2026, 10, 1)).CohortSize);
    }

    [Fact]
    public void StatusAtCutoff_UnknownWhenNothingWasRecordedYet()
    {
        Add("Nachgetragen", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(10, 15))); // recorded after the cutoff

        var result = Analyze(cutoff: new DateOnly(2026, 10, 1));

        Assert.Equal(1, result.Outcomes.Unknown);
    }

    // ---------- Consistency & small samples ----------

    [Fact]
    public void EveryCount_EqualsItsDrillDownList()
    {
        for (var i = 0; i < 12; i++)
        {
            var app = Add($"Firma {i}", new DateOnly(2026, 9, 1 + i),
                i % 3 == 0 ? [(ApplicationStatus.Beworben, At(9, 1 + i)), (ApplicationStatus.Vorstellungsgespraech, At(9, 20))]
                           : [(ApplicationStatus.Beworben, At(9, 1 + i))]);
            if (i % 4 == 0) AddInterview(app, At(9, 22));
        }

        var result = Analyze();

        foreach (var milestone in result.Milestones)
        {
            var drillDown = result.Applications.Where(a => a.Evidence.Any(e => e.Milestone == milestone.Milestone)).Select(a => a.Id);
            Assert.Equal(milestone.Count, milestone.ApplicationIds.Count);
            Assert.Equal(drillDown.Order(), milestone.ApplicationIds.Order());
            Assert.Equal(milestone.ApplicationIds.Count, milestone.ApplicationIds.Distinct().Count()); // distinct
        }
        // status at i = 0,3,6,9 and interview entries at i = 0,4,8 → {0,3,4,6,8,9}; i = 0 counted once
        Assert.Equal(6, result.Milestones.Single(m => m.Milestone == Milestone.Interview).Count);
        Assert.NotNull(result.Milestones[0].Percent); // 12 ≥ 10 → percentages shown
    }

    [Fact]
    public void SmallCohort_HasNoPercentages()
    {
        Add("Eins", new DateOnly(2026, 9, 1), (ApplicationStatus.Beworben, At(9, 1)));

        Assert.All(Analyze().Milestones, m => Assert.Null(m.Percent));
    }

    [Fact]
    public void EmptyData_GivesZeros()
    {
        var result = Analyze();

        Assert.Equal(0, result.CohortSize);
        Assert.All(result.Milestones, m => Assert.Equal(0, m.Count));
        Assert.Empty(result.Applications);
    }

    // ---------- Through the database ----------

    [Fact]
    public async Task Service_UsesRealHistoryAndInterviews()
    {
        var clock = new FixedClock(new DateTimeOffset(Now));
        using (var db = _database.CreateContext())
        {
            var applications = new ApplicationService(db, clock, _database.Storage);
            var app = await applications.CreateAsync(new ApplicationInput("Nordlicht", "Azubi", null, null, null,
                new DateOnly(2026, 10, 1), ApplicationStatus.Beworben, "Private Notiz"));
            await new InterviewService(db, clock).CreateAsync(app.Id,
                new InterviewInput(new DateTime(2026, 10, 25, 10, 0, 0), InterviewType.Video, null, null, null));
        }

        using var readDb = _database.CreateContext();
        var result = await new AnalysisService(readDb, clock).GetAnalysisAsync(null, null, null);

        Assert.Equal(1, M(result, Milestone.Interview).Count); // planned interview = invitation recorded
        Assert.Equal(ApplicationStatus.Beworben, result.Applications.Single().CurrentStatus); // status untouched
        Assert.DoesNotContain(typeof(AnalysisApplication).GetProperties(), p => p.Name is "Notes" or "Location");
    }
}
