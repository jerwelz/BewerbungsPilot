using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

public static class AnalysisEndpoints
{
    public static void MapAnalysisEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/analysis?from=2026-01-01&to=2026-10-22&cutoff=2026-10-22   (all optional)
        app.MapGet("/api/analysis", GetAnalysis);
    }

    private static async Task<IResult> GetAnalysis(DateOnly? from, DateOnly? to, DateOnly? cutoff, AnalysisService service)
    {
        var errors = new Dictionary<string, string[]>();
        if (from is not null && to is not null && to < from)
            errors["to"] = ["Das Ende des Zeitraums liegt vor dem Anfang."];
        if (cutoff > service.Today)
            errors["cutoff"] = ["Der Auswertungsstand kann nicht in der Zukunft liegen."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Ungültige Auswahl.");

        return Results.Ok(await service.GetAnalysisAsync(from, to, cutoff));
    }
}
