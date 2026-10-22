using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Rules that keep the data isolation intact when the code changes later (docs/security.md).
public class SecurityConventionTests
{
    private static string SourceDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "BewerbungsPilot"));

    // APIs that bypass the query filters or the ownership guard, or touch files directly –
    // allowed ONLY in the listed files. A new use elsewhere needs a deliberate, reviewed change here.
    public static TheoryData<string, string[]> RestrictedApis => new()
    {
        { @"IgnoreQueryFilters", ["Logic/LegacyDataClaim.cs"] },
        { @"ExecuteUpdate", ["Logic/LegacyDataClaim.cs"] },
        { @"ExecuteDelete", [] },
        { @"ExecuteSql|FromSql|SqlQuery|GetDbConnection|SqliteConnection|SqliteCommand|CreateCommand\(", ["Data/MigrationSafety.cs"] },
        { @"\bFile\.|\bDirectory\.|FileStream|FileInfo|DirectoryInfo", ["Logic/DocumentStorage.cs", "Data/MigrationSafety.cs", "Program.cs"] },
        { @"AddDbContextPool|AddPooledDbContextFactory", [] }, // the context holds the current user
    };

    [Theory]
    [MemberData(nameof(RestrictedApis))]
    public void RestrictedApi_IsOnlyUsedInAllowedFiles(string pattern, string[] allowed)
    {
        var root = SourceDirectory();
        var offenders = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("Data/Migrations/") && !f.StartsWith("bin/") && !f.StartsWith("obj/"))
            .Where(f => !allowed.Contains(f))
            .Where(f => File.ReadLines(Path.Combine(root, f)).Any(line => !line.TrimStart().StartsWith("//") && Regex.IsMatch(line, pattern)))
            .ToList();
        Assert.True(offenders.Count == 0, $"'{pattern}' used in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void SourceScan_FindsTheSourceFiles()
    {
        Assert.True(File.Exists(Path.Combine(SourceDirectory(), "Data", "AppDbContext.cs")));
    }

    // Every table with user data (owner column or link to an application) must have a query filter.
    [Fact]
    public void EveryOwnedEntity_HasAQueryFilter()
    {
        using var database = new TestDatabase();
        using var db = database.CreateContext();
        var owned = db.Model.GetEntityTypes()
            .Where(t => t.FindProperty(nameof(JobApplication.OwnerId)) is not null || t.FindProperty(nameof(Interview.JobApplicationId)) is not null)
            .ToList();
        Assert.Equal(4, owned.Count);
        Assert.All(owned, t => Assert.NotEmpty(t.GetDeclaredQueryFilters()));
    }
}
