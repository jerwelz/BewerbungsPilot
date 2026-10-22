using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot;

// Operator commands, run on the computer that holds the data – never reachable over HTTP:
//   dotnet run --project src/BewerbungsPilot -- migrate
//   dotnet run --project src/BewerbungsPilot -- claim-legacy-data --email you@example.com
//   dotnet run --project src/BewerbungsPilot -- reset-password --email you@example.com
// Add --Storage:DataDirectory <folder> to work on another data folder (e.g. a copy).
public static class AdminCommands
{
    public static readonly string[] Names = ["migrate", "claim-legacy-data", "reset-password"];

    public static async Task<int> RunAsync(WebApplication app, string command, string dataDirectory)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        try
        {
            switch (command)
            {
                case "migrate":
                    return await MigrateAsync(db, services.GetRequiredService<DocumentStorage>(), dataDirectory);
                case "claim-legacy-data":
                    return await ClaimAsync(db, services, app.Configuration["email"]);
                case "reset-password":
                    return await ResetPasswordAsync(db, services.GetRequiredService<UserManager<IdentityUser>>(), app.Configuration["email"]);
                default:
                    Console.Error.WriteLine($"Unbekannter Befehl: {command}");
                    return 1;
            }
        }
        catch (MigrationSafetyException exception)
        {
            Console.Error.WriteLine($"FEHLER: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> MigrateAsync(AppDbContext db, DocumentStorage storage, string dataDirectory)
    {
        if (MigrationSafety.GetState(db) == MigrationSafety.State.NewDatabase)
        {
            db.Database.Migrate();
            Console.WriteLine("Neue Datenbank angelegt.");
            return 0;
        }
        await MigrationSafety.MigrateWithBackupAsync(db, storage, Path.Combine(dataDirectory, "backups"), Console.Out);
        return 0;
    }

    private static async Task<int> ClaimAsync(AppDbContext db, IServiceProvider services, string? email)
    {
        if (!RequireUpToDate(db) || !RequireEmail(email))
            return 1;
        var user = await services.GetRequiredService<UserManager<IdentityUser>>().FindByEmailAsync(email!);
        if (user is null)
        {
            Console.Error.WriteLine($"Es gibt kein Konto mit der E-Mail-Adresse {email}. Bitte zuerst registrieren.");
            return 1;
        }

        var claim = new LegacyDataClaim(db, services.GetRequiredService<DocumentStorage>());
        var summary = await claim.GetSummaryAsync(null);
        if (summary.Applications == 0)
        {
            Console.WriteLine("Es gibt keine Daten ohne Konto. Nichts zu tun.");
            return 0;
        }

        Console.WriteLine("Daten ohne Konto (aus der Zeit vor der Anmeldung):");
        PrintSummary(summary);
        Console.Write($"Diese Daten werden dem Konto {user.Email} zugeordnet. Zur Bestätigung die E-Mail-Adresse erneut eingeben: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), user.Email, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Abgebrochen. Es wurde nichts geändert.");
            return 1;
        }

        try
        {
            await claim.ClaimAsync(user.Id);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"FEHLER: {exception.Message}");
            return 2;
        }
        Console.WriteLine($"Fertig. Jetzt im Konto {user.Email}:");
        PrintSummary(await claim.GetSummaryAsync(user.Id));
        return 0;
    }

    private static async Task<int> ResetPasswordAsync(AppDbContext db, UserManager<IdentityUser> users, string? email)
    {
        if (!RequireUpToDate(db) || !RequireEmail(email))
            return 1;
        var user = await users.FindByEmailAsync(email!);
        if (user is null)
        {
            Console.Error.WriteLine($"Es gibt kein Konto mit der E-Mail-Adresse {email}.");
            return 1;
        }

        var password = ReadSecret("Neues Passwort (mind. 12 Zeichen): ");
        if (password != ReadSecret("Neues Passwort wiederholen: "))
        {
            Console.Error.WriteLine("Die Passwörter stimmen nicht überein. Es wurde nichts geändert.");
            return 1;
        }

        // Also ends every existing session of this account (new security stamp).
        var result = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), password);
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("Das Passwort erfüllt die Anforderungen nicht (mindestens 12 Zeichen). Es wurde nichts geändert.");
            return 1;
        }
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        Console.WriteLine($"Das Passwort für {user.Email} wurde zurückgesetzt; eine Sperre wurde aufgehoben.");
        return 0;
    }

    private static bool RequireUpToDate(AppDbContext db)
    {
        if (MigrationSafety.GetState(db) == MigrationSafety.State.UpToDate)
            return true;
        Console.Error.WriteLine("Die Datenbank ist nicht aktuell. Bitte zuerst \"migrate\" ausführen.");
        return false;
    }

    private static bool RequireEmail(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email))
            return true;
        Console.Error.WriteLine("Bitte die E-Mail-Adresse mit --email angeben.");
        return false;
    }

    private static void PrintSummary(LegacyDataClaim.Summary s)
    {
        Console.WriteLine($"  Bewerbungen: {s.Applications}, Verlaufseinträge: {s.Events}, Dokumente: {s.Documents}, Gespräche: {s.Interviews}");
        Console.WriteLine($"  Fristen: {s.Deadlines}, bestätigte Standorte: {s.ConfirmedLocations}, Dokumentdateien geprüft: {s.FilesOk} von {s.Documents}");
        foreach (var problem in s.FileProblems)
            Console.WriteLine($"  WARNUNG: {problem}");
    }

    // Reads a password without showing it (falls back to a plain line when input is redirected).
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? "";
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace && text.Length > 0)
                text.Length--;
            else if (!char.IsControl(key.KeyChar))
                text.Append(key.KeyChar);
        }
        Console.WriteLine();
        return text.ToString();
    }
}
