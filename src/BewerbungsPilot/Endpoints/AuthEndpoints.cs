using System.Net.Mail;
using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace BewerbungsPilot.Endpoints;

// Login, registration, logout and password change. All security work (password hashing,
// lockout, cookies, security stamps) is done by ASP.NET Core Identity – nothing custom here.
// Every POST needs the antiforgery token (ApiProtectionFilter), also login and registration.
public static class AuthEndpoints
{
    public const string RateLimitPolicy = "auth";
    private static readonly TimeSpan RememberMeLifetime = TimeSpan.FromDays(14);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AddEndpointFilter<ApiProtectionFilter>();

        group.MapGet("/me", Me).AllowAnonymous();
        group.MapGet("/antiforgery", GetAntiforgeryToken).AllowAnonymous();
        group.MapPost("/register", Register).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/login", Login).AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/logout", Logout).RequireAuthorization();
        group.MapPost("/password", ChangePassword).RequireAuthorization().RequireRateLimiting(RateLimitPolicy);
    }

    // GET /api/auth/me → 401 when not logged in
    private static async Task<IResult> Me(HttpContext context, UserManager<IdentityUser> users)
    {
        var user = context.User.Identity?.IsAuthenticated == true ? await users.GetUserAsync(context.User) : null;
        return user is null ? NotLoggedIn() : Results.Ok(new UserResponse(user.Id, user.Email!, await DemoAccounts.IsDemoUserAsync(users, user)));
    }

    // GET /api/auth/antiforgery → sets the antiforgery cookie, returns the matching request token.
    // The token is bound to the logged-in user, so the browser fetches a new one after login/logout.
    private static IResult GetAntiforgeryToken(HttpContext context, IAntiforgery antiforgery, IConfiguration configuration, UserManager<IdentityUser> users)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new AntiforgeryResponse(tokens.RequestToken!, AllowRegistration(configuration), users.GetUserId(context.User),
            DemoAccounts.IsEnabled(configuration)));
    }

    // POST /api/auth/register
    private static async Task<IResult> Register(RegisterInput input, UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn, IConfiguration configuration)
    {
        if (!AllowRegistration(configuration))
            return Results.Problem(title: "Die Registrierung ist deaktiviert.", statusCode: StatusCodes.Status403Forbidden);

        var email = input.Email?.Trim() ?? "";
        var errors = new Dictionary<string, string[]>();
        if (!IsEmail(email))
            errors["email"] = ["Bitte geben Sie eine gültige E-Mail-Adresse an."];
        if (string.IsNullOrEmpty(input.Password))
            errors["password"] = ["Bitte geben Sie ein Passwort ein."];
        if (errors.Count > 0)
            return Results.ValidationProblem(errors, title: "Bitte prüfen Sie Ihre Eingaben.");

        var user = new IdentityUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, input.Password!);
        if (!result.Succeeded)
            return Results.ValidationProblem(TranslateErrors(result, "password"), title: "Bitte prüfen Sie Ihre Eingaben.");

        await signIn.SignInAsync(user, isPersistent: false);
        return Results.Created("/api/auth/me", new UserResponse(user.Id, email));
    }

    // POST /api/auth/login. Same answer for unknown e-mail and wrong password.
    private static async Task<IResult> Login(LoginInput input, UserManager<IdentityUser> users,
        SignInManager<IdentityUser> signIn, IPasswordHasher<IdentityUser> hasher, TimeProvider clock)
    {
        var email = input.Email?.Trim() ?? "";
        var user = IsEmail(email) ? await users.FindByEmailAsync(email) : null;
        if (user is null)
        {
            // Spend the same time as for a real password check, so response times do not reveal accounts.
            hasher.VerifyHashedPassword(DummyUser, DummyHash.Value, input.Password ?? "");
            return InvalidCredentials();
        }

        var result = await signIn.CheckPasswordSignInAsync(user, input.Password ?? "", lockoutOnFailure: true);
        if (result.IsLockedOut)
            return Results.Problem(
                title: "Zu viele Fehlversuche. Die Anmeldung ist für 15 Minuten gesperrt.",
                statusCode: StatusCodes.Status400BadRequest);
        if (!result.Succeeded)
            return InvalidCredentials();

        // "Angemeldet bleiben": fixed 14 days. Otherwise: browser session with sliding expiration.
        var properties = input.RememberMe
            ? new AuthenticationProperties { IsPersistent = true, AllowRefresh = false, ExpiresUtc = clock.GetUtcNow().Add(RememberMeLifetime) }
            : new AuthenticationProperties { IsPersistent = false };
        await signIn.SignInAsync(user, properties);
        return Results.Ok(new UserResponse(user.Id, user.Email!));
    }

    // POST /api/auth/logout. A new security stamp makes the old login cookie worthless on the
    // server too (also a copied one) – this ends every session of the account, on all devices.
    private static async Task<IResult> Logout(HttpContext context, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is not null)
            await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        return Results.NoContent();
    }

    // POST /api/auth/password. Changing the password ends all OTHER sessions of this account
    // (new security stamp); this session stays logged in.
    private static async Task<IResult> ChangePassword(PasswordChangeInput input, HttpContext context,
        UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn)
    {
        var user = await users.GetUserAsync(context.User);
        if (user is null)
            return NotLoggedIn();
        if (string.IsNullOrEmpty(input.CurrentPassword) || string.IsNullOrEmpty(input.NewPassword))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [string.IsNullOrEmpty(input.CurrentPassword) ? "currentPassword" : "newPassword"] = ["Bitte füllen Sie dieses Feld aus."]
            }, title: "Bitte prüfen Sie Ihre Eingaben.");

        var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded)
            return Results.ValidationProblem(TranslateErrors(result, "newPassword"), title: "Bitte prüfen Sie Ihre Eingaben.");

        await signIn.RefreshSignInAsync(user);
        return Results.NoContent();
    }

    // ---------- Helpers ----------

    private static readonly IdentityUser DummyUser = new();
    private static readonly Lazy<string> DummyHash =
        new(() => new PasswordHasher<IdentityUser>().HashPassword(DummyUser, Guid.NewGuid().ToString()));

    // Never in demo mode: the demo database must only contain demo accounts.
    private static bool AllowRegistration(IConfiguration configuration) =>
        configuration.GetValue<bool>("Auth:AllowRegistration") && !DemoAccounts.IsEnabled(configuration);

    private static bool IsEmail(string email) =>
        email.Length is > 0 and <= 256 && MailAddress.TryCreate(email, out var address)
        && address.Address == email && address.Host.Contains('.');

    // Identity error codes → German messages, grouped by form field.
    private static Dictionary<string, string[]> TranslateErrors(IdentityResult result, string passwordField)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
                errors[field] = list = [];
            if (!list.Contains(message))
                list.Add(message);
        }

        foreach (var error in result.Errors)
        {
            switch (error.Code)
            {
                case "DuplicateUserName" or "DuplicateEmail":
                    Add("email", "Für diese E-Mail-Adresse gibt es bereits ein Konto.");
                    break;
                case "InvalidUserName" or "InvalidEmail":
                    Add("email", "Bitte geben Sie eine gültige E-Mail-Adresse an.");
                    break;
                case "PasswordMismatch":
                    Add("currentPassword", "Das aktuelle Passwort ist falsch.");
                    break;
                case "PasswordTooShort":
                    Add(passwordField, "Das Passwort muss mindestens 12 Zeichen lang sein.");
                    break;
                default:
                    Add(passwordField, "Das Passwort erfüllt die Anforderungen nicht.");
                    break;
            }
        }
        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }

    private static IResult InvalidCredentials() =>
        Results.Problem(title: "E-Mail-Adresse oder Passwort ist falsch.", statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotLoggedIn() =>
        Results.Problem(title: "Bitte melden Sie sich an.", statusCode: StatusCodes.Status401Unauthorized);
}
