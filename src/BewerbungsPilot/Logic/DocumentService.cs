using System.Security.Cryptography;
using BewerbungsPilot.Data;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Logic;

// Business rules for documents: validation, metadata in SQLite, files via DocumentStorage.
//
// Access to a document always goes through GetAsync(id). When user accounts are
// added later, the ownership check belongs there – the storage stays the same.
public class DocumentService
{
    public const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB
    private const string PdfContentType = "application/pdf";
    private static readonly byte[] PdfSignature = "%PDF-"u8.ToArray(); // first bytes of every PDF

    private readonly AppDbContext _db;
    private readonly DocumentStorage _storage;
    private readonly TimeProvider _clock;

    public DocumentService(AppDbContext db, DocumentStorage storage, TimeProvider clock)
    {
        _db = db;
        _storage = storage;
        _clock = clock;
    }

    // Returns field name → error messages. An empty dictionary means "valid".
    public static Dictionary<string, string[]> ValidateUpload(string? fileName, byte[]? content, string? category)
    {
        var errors = new Dictionary<string, string[]>();

        if (!Enum.TryParse<DocumentCategory>(category, out var parsed) || !Enum.IsDefined(parsed))
            errors["category"] = ["Bitte wählen Sie eine Kategorie aus."];

        if (content is null || string.IsNullOrWhiteSpace(fileName))
            errors["file"] = ["Bitte wählen Sie eine Datei aus."];
        else if (content.Length == 0)
            errors["file"] = ["Die Datei ist leer."];
        else if (content.Length > MaxFileSizeBytes)
            errors["file"] = ["Die Datei ist zu groß. Erlaubt sind höchstens 10 MB."];
        else if (!Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            errors["file"] = ["Es werden nur PDF-Dateien unterstützt."];
        // The extension alone proves nothing: a renamed .exe would pass. So we check the
        // file's signature ("magic bytes") – every real PDF starts with "%PDF-".
        else if (!content.AsSpan().StartsWith(PdfSignature))
            errors["file"] = ["Die Datei ist keine gültige PDF-Datei."];

        return errors;
    }

    // Saves a validated upload. Returns null if the application does not exist.
    public async Task<ApplicationDocument?> UploadAsync(int applicationId, string category, string fileName, byte[] content)
    {
        var application = await _db.Applications.FindAsync(applicationId);
        if (application is null)
            return null;

        // 1. Write the file first …
        var storedFileName = await _storage.SaveAsync(content, ".pdf");
        var now = _clock.GetUtcNow().UtcDateTime;
        var document = new ApplicationDocument
        {
            JobApplicationId = applicationId,
            Category = Enum.Parse<DocumentCategory>(category),
            OriginalFileName = CleanFileName(fileName),
            StoredFileName = storedFileName,
            ContentType = PdfContentType,
            SizeBytes = content.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
            UploadedAt = now
        };

        _db.Documents.Add(document);
        application.Events.Add(new ApplicationEvent
        {
            Type = EventType.DocumentAdded,
            Description = $"Dokument hinzugefügt: {document.Category} ({document.OriginalFileName})",
            OccurredAt = now
        });

        // 2. … then the metadata. If the database fails, remove the file again,
        // so no file is left behind without a record.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch
        {
            _storage.TryDelete(storedFileName);
            throw;
        }
        return document;
    }

    public async Task<List<ApplicationDocument>> GetForApplicationAsync(int applicationId)
    {
        return await _db.Documents
            .Where(d => d.JobApplicationId == applicationId)
            .OrderByDescending(d => d.UploadedAt)
            .ToListAsync();
    }

    // The single entry point for reading a document (see comment at the top).
    public async Task<ApplicationDocument?> GetAsync(int documentId)
    {
        return await _db.Documents.FindAsync(documentId);
    }

    // Returns null if the file is missing on disk.
    public Stream? OpenFile(ApplicationDocument document) => _storage.OpenRead(document.StoredFileName);

    // Deletes metadata and file. Returns false if the id does not exist.
    public async Task<bool> DeleteAsync(int documentId)
    {
        var document = await _db.Documents.Include(d => d.JobApplication).FirstOrDefaultAsync(d => d.Id == documentId);
        if (document is null)
            return false;

        _db.Documents.Remove(document);
        document.JobApplication!.Events.Add(new ApplicationEvent
        {
            Type = EventType.DocumentDeleted,
            Description = $"Dokument gelöscht: {document.Category} ({document.OriginalFileName})",
            OccurredAt = _clock.GetUtcNow().UtcDateTime
        });

        // Database first, file second: if deleting the file fails, only an unused file
        // remains (harmless). The other order could leave a record pointing to nothing.
        await _db.SaveChangesAsync();
        _storage.TryDelete(document.StoredFileName);
        return true;
    }

    // "C:\Users\me\..\Lebenslauf.pdf" → "Lebenslauf.pdf"; removes control characters.
    public static string CleanFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 255)
            name = name[^255..];
        return name == "" ? "Dokument.pdf" : name;
    }
}
