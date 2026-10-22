using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

// HTTP layer for interviews and the calendar. Rules live in InterviewService / ScheduleService.
public static class InterviewEndpoints
{
    public static void MapInterviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/applications/{id:int}/interviews", Create);
        app.MapPut("/api/interviews/{interviewId:int}", Update);
        app.MapDelete("/api/interviews/{interviewId:int}", Delete);

        app.MapGet("/api/calendar", GetCalendar);
        app.MapGet("/api/calendar/upcoming", GetUpcoming);
    }

    // POST /api/applications/5/interviews
    private static async Task<IResult> Create(int id, InterviewInput input, InterviewService service, ApplicationService applications)
    {
        var errors = service.Validate(input);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var interview = await service.CreateAsync(id, input);
        return interview is null
            ? Results.Problem(title: "Diese Bewerbung wurde nicht gefunden. Möglicherweise wurde sie bereits gelöscht.",
                statusCode: StatusCodes.Status404NotFound)
            : Results.Created($"/api/interviews/{interview.Id}", InterviewResponse.FromEntity(interview, applications.Today));
    }

    // PUT /api/interviews/7
    private static async Task<IResult> Update(int interviewId, InterviewInput input, InterviewService service, ApplicationService applications)
    {
        var existing = await service.GetAsync(interviewId);
        if (existing is null)
            return InterviewNotFound();

        var errors = service.Validate(input, existing);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var updated = await service.UpdateAsync(interviewId, input);
        return updated is null ? InterviewNotFound() : Results.Ok(InterviewResponse.FromEntity(updated, applications.Today));
    }

    // DELETE /api/interviews/7
    private static async Task<IResult> Delete(int interviewId, InterviewService service) =>
        await service.DeleteAsync(interviewId) ? Results.NoContent() : InterviewNotFound();

    // GET /api/calendar?from=2026-09-28&to=2026-11-08
    private static async Task<IResult> GetCalendar(DateOnly from, DateOnly to, ScheduleService service)
    {
        if (to < from || to.DayNumber - from.DayNumber > ScheduleService.MaxRangeDays)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["to"] = [$"Der Zeitraum muss zwischen 1 und {ScheduleService.MaxRangeDays} Tagen liegen."] },
                title: "Ungültiger Zeitraum.");
        return Results.Ok(await service.GetEventsAsync(from, to));
    }

    // GET /api/calendar/upcoming
    private static async Task<IResult> GetUpcoming(ScheduleService service) =>
        Results.Ok(await service.GetUpcomingAsync());

    private static IResult InterviewNotFound() =>
        Results.Problem(title: "Dieses Gespräch wurde nicht gefunden. Möglicherweise wurde es bereits gelöscht.",
            statusCode: StatusCodes.Status404NotFound);
}
