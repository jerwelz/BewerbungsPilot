using BewerbungsPilot.Logic;

namespace BewerbungsPilot.Endpoints;

// HTTP layer: translates HTTP requests into service calls and results into HTTP responses.
// No business rules here – those live in ApplicationService.
public static class ApplicationEndpoints
{
    public static void MapApplicationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/applications");

        group.MapGet("/", GetAll);
        group.MapGet("/{id:int}", GetById);
        group.MapPost("/", Create);
    }

    // GET /api/applications
    private static async Task<IResult> GetAll(ApplicationService service)
    {
        var applications = await service.GetAllAsync();
        return Results.Ok(applications.Select(ApplicationResponse.FromEntity));
    }

    // GET /api/applications/5
    private static async Task<IResult> GetById(int id, ApplicationService service)
    {
        var application = await service.GetByIdAsync(id);
        return application is null
            ? Results.NotFound()
            : Results.Ok(ApplicationResponse.FromEntity(application));
    }

    // POST /api/applications   (JSON body = ApplicationInput)
    private static async Task<IResult> Create(ApplicationInput input, ApplicationService service)
    {
        var errors = service.Validate(input);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var created = await service.CreateAsync(input);
        return Results.Created($"/api/applications/{created.Id}", ApplicationResponse.FromEntity(created));
    }
}
