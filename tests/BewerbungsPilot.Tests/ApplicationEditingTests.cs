using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

// Editing, deleting and duplicate detection.
public class ApplicationEditingTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private static ApplicationInput Input(string company = "Nordlicht Software GmbH", string position = "Auszubildender FIAE",
        ApplicationStatus status = ApplicationStatus.Beworben) =>
        new(company, position, "Hamburg", null, null, new DateOnly(2026, 10, 1), status, null);

    private async Task<int> CreateAsync(ApplicationInput input)
    {
        using var db = _database.CreateContext();
        return (await new ApplicationService(db, _clock).CreateAsync(input)).Id;
    }

    [Fact]
    public async Task Update_ChangedStatus_SavesStatusAndHistoryEntryTogether()
    {
        var id = await CreateAsync(Input());

        using (var db = _database.CreateContext())
        {
            await new ApplicationService(db, _clock).UpdateAsync(id, Input(status: ApplicationStatus.InPruefung));
        }

        using var readDb = _database.CreateContext();
        var saved = await readDb.Applications.Include(a => a.Events).SingleAsync(a => a.Id == id);
        Assert.Equal(ApplicationStatus.InPruefung, saved.Status);
        Assert.Equal(2, saved.Events.Count); // "created" + "status changed"

        var change = saved.Events.Single(e => e.Type == EventType.StatusChanged);
        Assert.Equal(ApplicationStatus.Beworben, change.FromStatus);
        Assert.Equal(ApplicationStatus.InPruefung, change.ToStatus);
        Assert.Equal("Status geändert: Beworben → In Prüfung", change.Description);
    }

    [Fact]
    public async Task Update_UnchangedStatus_ChangesFieldsWithoutHistoryEntry()
    {
        var id = await CreateAsync(Input());

        using (var db = _database.CreateContext())
        {
            var input = Input(position: "Fachinformatiker Anwendungsentwicklung") with { Notes = "Ansprechpartnerin: Frau Berg" };
            await new ApplicationService(db, _clock).UpdateAsync(id, input);
        }

        using var readDb = _database.CreateContext();
        var saved = await readDb.Applications.Include(a => a.Events).SingleAsync(a => a.Id == id);
        Assert.Equal("Fachinformatiker Anwendungsentwicklung", saved.Position);
        Assert.Equal("Ansprechpartnerin: Frau Berg", saved.Notes);
        Assert.Single(saved.Events);
    }

    [Fact]
    public async Task Update_NonexistentId_ReturnsNull()
    {
        using var db = _database.CreateContext();

        Assert.Null(await new ApplicationService(db, _clock).UpdateAsync(999, Input()));
    }

    [Fact]
    public async Task Delete_RemovesApplicationAndItsHistory()
    {
        var id = await CreateAsync(Input());

        using (var db = _database.CreateContext())
        {
            Assert.True(await new ApplicationService(db, _clock).DeleteAsync(id));
        }

        using var readDb = _database.CreateContext();
        Assert.Empty(await readDb.Applications.ToListAsync());
        Assert.Empty(await readDb.ApplicationEvents.ToListAsync());
    }

    [Fact]
    public async Task Delete_NonexistentId_ReturnsFalse()
    {
        using var db = _database.CreateContext();

        Assert.False(await new ApplicationService(db, _clock).DeleteAsync(999));
    }

    [Theory]
    [InlineData("Nordlicht Software GmbH", "Auszubildender FIAE")]
    [InlineData("  nordlicht   SOFTWARE gmbh ", "auszubildender fiae")]
    public async Task FindDuplicates_SameCompanyAndPosition_IgnoringCaseAndSpaces(string company, string position)
    {
        await CreateAsync(Input());
        using var db = _database.CreateContext();

        var duplicates = await new ApplicationService(db, _clock).FindDuplicatesAsync(company, position, null);

        Assert.Single(duplicates);
    }

    [Fact]
    public async Task FindDuplicates_HandlesUmlautsCaseInsensitively()
    {
        await CreateAsync(Input(company: "Müller Logistik AG"));
        using var db = _database.CreateContext();

        var duplicates = await new ApplicationService(db, _clock).FindDuplicatesAsync("MÜLLER LOGISTIK AG", "Auszubildender FIAE", null);

        Assert.Single(duplicates);
    }

    [Fact]
    public async Task FindDuplicates_DifferentPosition_IsNoDuplicate()
    {
        await CreateAsync(Input());
        using var db = _database.CreateContext();

        var duplicates = await new ApplicationService(db, _clock).FindDuplicatesAsync("Nordlicht Software GmbH", "Werkstudent IT", null);

        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task FindDuplicates_WhenEditing_ExcludesTheApplicationItself()
    {
        var id = await CreateAsync(Input());
        using var db = _database.CreateContext();

        var duplicates = await new ApplicationService(db, _clock).FindDuplicatesAsync("Nordlicht Software GmbH", "Auszubildender FIAE", excludeId: id);

        Assert.Empty(duplicates);
    }
}
