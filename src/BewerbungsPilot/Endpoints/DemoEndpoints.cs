using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Identity;

namespace BewerbungsPilot.Endpoints;

// "Demo starten" (only mapped when Demo:Enabled is true, see Program.cs). Creates a fresh demo
// account with fictional data and signs the visitor in. Anonymous like the login, and protected
// the same way: antiforgery token + rate limit.
public static class DemoEndpoints
{
    public const string RateLimitPolicy = "demo";

    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/demo").AddEndpointFilter<ApiProtectionFilter>();
        group.MapPost("/start", Start).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
    }

    // POST /api/demo/start
    private static async Task<IResult> Start(DemoAccounts demo, SignInManager<IdentityUser> signIn, ILogger<DemoAccounts> logger)
    {
        var user = await demo.CreateAsync();
        if (user is null)
        {
            logger.LogWarning("Demo-Start abgelehnt: Höchstzahl gleichzeitiger Demo-Konten erreicht.");
            return Results.Problem(title: "Die Demo ist gerade ausgelastet. Bitte versuchen Sie es später erneut.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        await signIn.SignInAsync(user, isPersistent: false);
        logger.LogInformation("Demo-Konto erstellt."); // no id or address: counting starts in the log is enough
        return Results.Ok(new UserResponse(user.Id, user.Email!, IsDemo: true));
    }
}
