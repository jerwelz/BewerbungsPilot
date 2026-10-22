using BewerbungsPilot.Data;
using Microsoft.AspNetCore.Antiforgery;

namespace BewerbungsPilot.Endpoints;

// Runs around every API endpoint (registered in Program.cs and AuthEndpoints.cs).
// - CSRF: every request that changes something (POST/PUT/PATCH/DELETE) must send the antiforgery
//   token in the X-CSRF-TOKEN header. Another website can make the browser send the login
//   cookie, but it cannot read the token.
// - Ownership: an OwnershipViolationException from the database guard becomes 404, exactly
//   like an id that does not exist.
public class ApiProtectionFilter : IEndpointFilter
{
    private readonly IAntiforgery _antiforgery;
    private readonly ILogger<ApiProtectionFilter> _logger;

    public ApiProtectionFilter(IAntiforgery antiforgery, ILogger<ApiProtectionFilter> logger)
    {
        _antiforgery = antiforgery;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var method = http.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            try
            {
                await _antiforgery.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Problem(
                    title: "Die Sicherheitsprüfung ist fehlgeschlagen. Bitte laden Sie die Seite neu.",
                    statusCode: StatusCodes.Status403Forbidden,
                    extensions: new Dictionary<string, object?> { ["code"] = "csrf" });
            }
        }

        try
        {
            return await next(context);
        }
        catch (OwnershipViolationException exception)
        {
            _logger.LogWarning("Zugriff auf fremde Daten abgewiesen: {Reason}", exception.Message);
            return Results.Problem(title: "Der Datensatz wurde nicht gefunden.", statusCode: StatusCodes.Status404NotFound);
        }
    }
}
