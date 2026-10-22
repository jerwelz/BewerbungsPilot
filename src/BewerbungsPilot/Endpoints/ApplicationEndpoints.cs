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
        group.MapGet("/duplicates", FindDuplicates);
        group.MapPost("/", Create);
        group.MapPut("/{id:int}", Update);
        group.MapPatch("/{id:int}/status", ChangeStatus);
        group.MapDelete("/{id:int}", Delete);
    }

    // GET /api/applications
    private static async Task<IResult> GetAll(ApplicationService service)
    {
        var applications = await service.GetAllAsync();
        return Results.Ok(applications.Select(a => ApplicationResponse.FromEntity(a, service.Today)));
    }

    // GET /api/applications/5   → application + history
    private static async Task<IResult> GetById(int id, ApplicationService service)
    {
        var application = await service.GetByIdAsync(id);
        return application is null
            ? NotFound()
            : Results.Ok(ApplicationDetailResponse.FromEntity(application, service.Today));
    }

    // GET /api/applications/duplicates?companyName=...&position=...&excludeId=5
    private static async Task<IResult> FindDuplicates(
        string? companyName, string? position, int? excludeId, ApplicationService service)
    {
        var duplicates = await service.FindDuplicatesAsync(companyName, position, excludeId);
        return Results.Ok(duplicates.Select(d =>
            new DuplicateResponse(d.Id, d.CompanyName, d.Position, d.Status, d.CreatedAt)));
    }

    // POST /api/applications   (JSON body = ApplicationInput)
    private static async Task<IResult> Create(ApplicationInput input, ApplicationService service)
    {
        var errors = service.Validate(input);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var created = await service.CreateAsync(input);
        return Results.Created($"/api/applications/{created.Id}", ApplicationResponse.FromEntity(created, service.Today));
    }

    // PUT /api/applications/5   (JSON body = ApplicationInput)
    private static async Task<IResult> Update(int id, ApplicationInput input, ApplicationService service)
    {
        var errors = service.Validate(input);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var updated = await service.UpdateAsync(id, input);
        return updated is null
            ? NotFound()
            : Results.Ok(ApplicationDetailResponse.FromEntity(updated, service.Today));
    }

    // PATCH /api/applications/5/status   (JSON body: { "status": "InPruefung" })
    private static async Task<IResult> ChangeStatus(int id, StatusChangeInput input, ApplicationService service)
    {
        if (!Enum.IsDefined(input.Status))
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { ["status"] = ["Unbekannter Status."] },
                title: "Bitte prüfen Sie Ihre Eingaben.");

        var updated = await service.ChangeStatusAsync(id, input.Status);
        return updated is null
            ? NotFound()
            : Results.Ok(ApplicationResponse.FromEntity(updated, service.Today));
    }

    // DELETE /api/applications/5
    private static async Task<IResult> Delete(int id, ApplicationService service)
    {
        var deleted = await service.DeleteAsync(id);
        return deleted ? Results.NoContent() : NotFound();
    }

    private static IResult NotFound() =>
        Results.Problem(title: "Diese Bewerbung wurde nicht gefunden. Möglicherweise wurde sie bereits gelöscht.",
            statusCode: StatusCodes.Status404NotFound);
}
