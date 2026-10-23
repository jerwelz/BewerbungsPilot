using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Identity;

namespace BewerbungsPilot.Endpoints;

// "Demo starten" (only mapped when Demo:Enabled is true, see Program.cs). Creates a fresh demo
// account with fictional data and signs the visitor in. Anonymous like the login, and protected
// the same way: antiforgery token + rate limit.
public static class DemoEndpoints
{
    public static void MapDemoEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/demo").AddEndpointFilter<ApiProtectionFilter>();
        group.MapPost("/start", Start).AllowAnonymous().RequireRateLimiting(AuthEndpoints.RateLimitPolicy);
    }

    // POST /api/demo/start
    private static async Task<IResult> Start(DemoAccounts demo, SignInManager<IdentityUser> signIn)
    {
        var user = await demo.CreateAsync();
        if (user is null)
            return Results.Problem(title: "Die Demo ist gerade ausgelastet. Bitte versuchen Sie es später erneut.",
                statusCode: StatusCodes.Status429TooManyRequests);

        await signIn.SignInAsync(user, isPersistent: false);
        return Results.Ok(new UserResponse(user.Id, user.Email!, IsDemo: true));
    }
}
