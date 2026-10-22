using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

public class ApplicationServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private static ApplicationInput ValidInput(ApplicationStatus status = ApplicationStatus.Entwurf, DateOnly? appliedOn = null) =>
        new("Nordlicht Software GmbH", "Auszubildender Fachinformatiker AE", "Hamburg",
            "https://example.org/stelle", new DateOnly(2026, 11, 30), appliedOn, status, null);

    [Fact]
    public async Task Create_SavesApplicationWithCreatedEvent_AndPersistsIt()
    {
        using (var db = _database.CreateContext())
        {
            await new ApplicationService(db, _clock, _database.Storage).CreateAsync(ValidInput());
        }

        // Read back with a NEW context to prove the data really is in the database.
        using var readDb = _database.CreateContext();
        var saved = await readDb.Applications.Include(a => a.Events).SingleAsync();

        Assert.Equal("Nordlicht Software GmbH", saved.CompanyName);
        Assert.Equal(ApplicationStatus.Entwurf, saved.Status);
        var createdEvent = Assert.Single(saved.Events);
        Assert.Equal(EventType.Created, createdEvent.Type);
        Assert.Equal(ApplicationStatus.Entwurf, createdEvent.ToStatus);
    }

    [Fact]
    public async Task Create_SubmittedWithoutDate_SetsAppliedOnToToday()
    {
        using var db = _database.CreateContext();

        var created = await new ApplicationService(db, _clock, _database.Storage).CreateAsync(ValidInput(ApplicationStatus.Beworben));

        Assert.Equal(new DateOnly(2026, 10, 22), created.AppliedOn);
    }

    [Fact]
    public void Validate_MissingRequiredFields_ReturnsGermanMessages()
    {
        using var db = _database.CreateContext();
        var input = ValidInput() with { CompanyName = "  ", Position = null };

        var errors = new ApplicationService(db, _clock, _database.Storage).Validate(input);

        Assert.Equal("Bitte geben Sie das Unternehmen an.", Assert.Single(errors["companyName"]));
        Assert.Equal("Bitte geben Sie die Position an.", Assert.Single(errors["position"]));
    }

    [Fact]
    public void Validate_AppliedOnInFuture_IsRejected()
    {
        using var db = _database.CreateContext();
        var input = ValidInput(ApplicationStatus.Beworben, appliedOn: new DateOnly(2026, 10, 23));

        var errors = new ApplicationService(db, _clock, _database.Storage).Validate(input);

        Assert.True(errors.ContainsKey("appliedOn"));
    }

    [Theory]
    [InlineData("example.org")]
    [InlineData("ftp://example.org/datei")]
    [InlineData("javascript:alert(1)")]
    public void Validate_InvalidJobUrl_IsRejected(string url)
    {
        using var db = _database.CreateContext();

        var errors = new ApplicationService(db, _clock, _database.Storage).Validate(ValidInput() with { JobUrl = url });

        Assert.True(errors.ContainsKey("jobUrl"));
    }

    [Fact]
    public void Validate_ValidInput_HasNoErrors()
    {
        using var db = _database.CreateContext();

        Assert.Empty(new ApplicationService(db, _clock, _database.Storage).Validate(ValidInput()));
    }
}
