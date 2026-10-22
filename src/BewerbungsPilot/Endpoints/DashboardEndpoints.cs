using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/dashboard → all numbers for the "Übersicht" page in one response
        app.MapGet("/api/dashboard", async (DashboardService service) =>
            Results.Ok(await service.GetDashboardAsync()));
    }
}
