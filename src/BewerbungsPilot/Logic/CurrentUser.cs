using System.Security.Claims;

namespace BewerbungsPilot.Logic;

// The user who sends the current request, taken from the login cookie.
// One instance per request (scoped). UserId is null for anonymous requests and for the
// command-line operations – the database then shows no private data at all (see AppDbContext).
public class CurrentUser
{
    private readonly Func<string?> _read;

    public CurrentUser(string? userId) => _read = () => userId;

    private CurrentUser(Func<string?> read) => _read = read;

    // Read on every access, not once: the request's DbContext can already be created while the
    // login cookie is still being checked (security stamp validation), before the user is known.
    public string? UserId => _read();

    public static CurrentUser FromHttpContext(IHttpContextAccessor accessor) =>
        new(() => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier));
}
