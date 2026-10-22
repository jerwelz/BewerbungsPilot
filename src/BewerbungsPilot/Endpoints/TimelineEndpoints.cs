using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

public static class TimelineEndpoints
{
    public static void MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/timeline", GetTimeline);
        app.MapGet("/api/waiting", GetWaitingQueue);
    }

    // GET /api/timeline                              → complete recorded history
    // GET /api/timeline?from=2026-07-01&to=2026-10-22 → only what overlaps this range
    private static async Task<IResult> GetTimeline(DateOnly? from, DateOnly? to, TimelineService service)
    {
        if (from is not null && to is not null && to < from)
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["to"] = ["Das Ende des Zeitraums liegt vor dem Anfang."] },
                title: "Ungültiger Zeitraum.");
        return Results.Ok(await service.GetTimelineAsync(from, to));
    }

    // GET /api/waiting?sort=SinceSubmission | SinceStatusChange
    private static async Task<IResult> GetWaitingQueue(WaitingSort? sort, WaitingQueueService service) =>
        Results.Ok(await service.GetQueueAsync(sort ?? WaitingSort.SinceSubmission));
}
