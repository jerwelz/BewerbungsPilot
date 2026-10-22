using System.Text;
using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Tests;

public class DocumentTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    // A tiny but real-looking PDF: starts with the "%PDF-" signature.
    private static byte[] Pdf(string text = "Lebenslauf") => Encoding.ASCII.GetBytes($"%PDF-1.7\n% {text}\n%%EOF\n");

    private async Task<int> CreateApplicationAsync(string company = "Nordlicht Software GmbH")
    {
        using var db = _database.CreateContext();
        var input = new ApplicationInput(company, "Auszubildender FIAE", null, null, null, null, ApplicationStatus.Entwurf, null);
        return (await new ApplicationService(db, _clock, _database.Storage).CreateAsync(input)).Id;
    }

    private async Task<ApplicationDocument?> UploadAsync(int applicationId, byte[] content,
        string fileName = "Lebenslauf.pdf", string category = "Lebenslauf")
    {
        using var db = _database.CreateContext();
        return await new DocumentService(db, _database.Storage, _clock).UploadAsync(applicationId, category, fileName, content);
    }

    private byte[]? ReadStoredFile(ApplicationDocument document)
    {
        using var stream = _database.Storage.OpenRead(document.StoredFileName);
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // ---------- Validation ----------

    [Fact]
    public void Validate_ValidPdf_HasNoErrors()
    {
        Assert.Empty(DocumentService.ValidateUpload("Lebenslauf.pdf", Pdf(), "Lebenslauf"));
    }

    [Fact]
    public void Validate_WrongExtension_IsRejected()
    {
        var errors = DocumentService.ValidateUpload("Lebenslauf.docx", Pdf(), "Lebenslauf");

        Assert.Equal("Es werden nur PDF-Dateien unterstützt.", Assert.Single(errors["file"]));
    }

    [Fact]
    public void Validate_RenamedExecutable_IsRejectedBySignature()
    {
        var exe = Encoding.ASCII.GetBytes("MZ\x90\0 this is not a PDF");

        var errors = DocumentService.ValidateUpload("Lebenslauf.pdf", exe, "Lebenslauf");

        Assert.Equal("Die Datei ist keine gültige PDF-Datei.", Assert.Single(errors["file"]));
    }

    [Fact]
    public void Validate_FileOver10MB_IsRejected()
    {
        var tooBig = new byte[DocumentService.MaxFileSizeBytes + 1];
        Pdf().CopyTo(tooBig, 0);

        var errors = DocumentService.ValidateUpload("Gross.pdf", tooBig, "Zeugnis");

        Assert.Contains("10 MB", Assert.Single(errors["file"]));
    }

    [Fact]
    public void Validate_FileOfExactly10MB_IsAccepted()
    {
        var maxSize = new byte[DocumentService.MaxFileSizeBytes];
        Pdf().CopyTo(maxSize, 0);

        Assert.Empty(DocumentService.ValidateUpload("Grenze.pdf", maxSize, "Zeugnis"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Passfoto")]
    [InlineData("99")]
    public void Validate_MissingOrUnknownCategory_IsRejected(string? category)
    {
        Assert.True(DocumentService.ValidateUpload("a.pdf", Pdf(), category).ContainsKey("category"));
    }

    [Fact]
    public void Validate_EmptyFile_IsRejected()
    {
        Assert.Equal("Die Datei ist leer.", Assert.Single(DocumentService.ValidateUpload("a.pdf", [], "Sonstiges")["file"]));
    }

    [Theory]
    [InlineData(@"C:\Users\me\..\Lebenslauf.pdf", "Lebenslauf.pdf")]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("Zeugnis\u0000\u0007.pdf", "Zeugnis.pdf")]
    public void CleanFileName_StripsPathsAndControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, DocumentService.CleanFileName(input));
    }

    // ---------- Upload, persistence, download ----------

    [Fact]
    public async Task Upload_SavesMetadataFileAndHistoryEntry()
    {
        var applicationId = await CreateApplicationAsync();
        var content = Pdf();

        var uploaded = await UploadAsync(applicationId, content, "Lebenslauf_Oktober.pdf");

        using var db = _database.CreateContext();
        var saved = await db.Documents.SingleAsync();
        Assert.Equal(uploaded!.Id, saved.Id);
        Assert.Equal(applicationId, saved.JobApplicationId);
        Assert.Equal("Lebenslauf_Oktober.pdf", saved.OriginalFileName);
        Assert.Equal(DocumentCategory.Lebenslauf, saved.Category);
        Assert.Equal(content.Length, saved.SizeBytes);
        Assert.Equal(64, saved.Sha256.Length);
        Assert.Matches("^[0-9a-f]{32}\\.pdf$", saved.StoredFileName);
        Assert.NotEqual(saved.OriginalFileName, saved.StoredFileName);

        Assert.Equal(content, ReadStoredFile(saved)); // download returns the exact bytes
        Assert.Contains(await db.ApplicationEvents.ToListAsync(), e => e.Type == EventType.DocumentAdded);
    }

    [Fact]
    public async Task Upload_MissingApplication_ReturnsNullAndStoresNothing()
    {
        Assert.Null(await UploadAsync(999, Pdf()));

        using var db = _database.CreateContext();
        Assert.Empty(await db.Documents.ToListAsync());
        Assert.Empty(Directory.GetFiles(_database.Storage.Directory));
    }

    [Fact]
    public async Task Upload_SameFileNameTwice_KeepsBothVersionsIndependently()
    {
        var applicationA = await CreateApplicationAsync("Firma A");
        var applicationB = await CreateApplicationAsync("Firma B");

        var october = await UploadAsync(applicationA, Pdf("Version Oktober"), "Lebenslauf.pdf");
        var november = await UploadAsync(applicationB, Pdf("Version November"), "Lebenslauf.pdf");

        Assert.NotEqual(october!.StoredFileName, november!.StoredFileName);
        Assert.Equal(Pdf("Version Oktober"), ReadStoredFile(october));
        Assert.Equal(Pdf("Version November"), ReadStoredFile(november));
        Assert.NotEqual(october.Sha256, november.Sha256);
    }

    // ---------- Reading & deleting ----------

    [Fact]
    public async Task GetAsync_InvalidId_ReturnsNull()
    {
        using var db = _database.CreateContext();

        Assert.Null(await new DocumentService(db, _database.Storage, _clock).GetAsync(999));
    }

    [Fact]
    public async Task Delete_RemovesMetadataAndFile_ButNotOtherDocuments()
    {
        var applicationId = await CreateApplicationAsync();
        var keep = await UploadAsync(applicationId, Pdf("behalten"), "Anschreiben.pdf", "Anschreiben");
        var remove = await UploadAsync(applicationId, Pdf("löschen"), "Alt.pdf", "Sonstiges");

        using (var db = _database.CreateContext())
        {
            Assert.True(await new DocumentService(db, _database.Storage, _clock).DeleteAsync(remove!.Id));
        }

        using var readDb = _database.CreateContext();
        Assert.Equal(keep!.Id, (await readDb.Documents.SingleAsync()).Id);
        Assert.Null(ReadStoredFile(remove));
        Assert.NotNull(ReadStoredFile(keep));
        Assert.Contains(await readDb.ApplicationEvents.ToListAsync(), e => e.Type == EventType.DocumentDeleted);
    }

    [Fact]
    public async Task Delete_InvalidId_ReturnsFalse()
    {
        using var db = _database.CreateContext();

        Assert.False(await new DocumentService(db, _database.Storage, _clock).DeleteAsync(999));
    }

    [Fact]
    public async Task DeletingApplication_RemovesItsDocumentFiles()
    {
        var applicationId = await CreateApplicationAsync();
        var document = await UploadAsync(applicationId, Pdf());

        using (var db = _database.CreateContext())
        {
            await new ApplicationService(db, _clock, _database.Storage).DeleteAsync(applicationId);
        }

        using var readDb = _database.CreateContext();
        Assert.Empty(await readDb.Documents.ToListAsync());
        Assert.Null(ReadStoredFile(document!));
    }

    // ---------- Storage safety ----------

    [Theory]
    [InlineData("../bewerbungspilot.db")]
    [InlineData("..\\..\\appsettings.json")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("lebenslauf.pdf")]
    public void Storage_RejectsNamesItDidNotGenerate(string storedFileName)
    {
        Assert.Throws<ArgumentException>(() => _database.Storage.OpenRead(storedFileName));
    }
}
