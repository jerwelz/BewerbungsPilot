using System.Text.RegularExpressions;

namespace BewerbungsPilot.Logic;

// Reads and writes document files in ONE private folder (storage/documents/).
// This class knows nothing about the database – it only handles files.
public partial class DocumentStorage
{
    public DocumentStorage(string directory)
    {
        Directory = Path.GetFullPath(directory);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    // Saves the bytes under a new random name and returns that name.
    // FileMode.CreateNew guarantees that an existing file is never overwritten.
    public async Task<string> SaveAsync(byte[] content, string extension)
    {
        var storedFileName = $"{Guid.NewGuid():N}{extension}";
        await using var file = new FileStream(PathFor(storedFileName), FileMode.CreateNew, FileAccess.Write);
        await file.WriteAsync(content);
        return storedFileName;
    }

    // Returns null if the file is missing.
    public Stream? OpenRead(string storedFileName)
    {
        var path = PathFor(storedFileName);
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    // Deletes a file. Returns false instead of throwing if that is not possible
    // (e.g. the file is open in another program) – the caller decides what to do.
    public bool TryDelete(string storedFileName)
    {
        try
        {
            var path = PathFor(storedFileName);
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Protection against path traversal ("../../appsettings.json"):
    // only names in exactly the format we generate (32 hex characters + extension)
    // are accepted, so a path can never point outside the documents folder.
    private string PathFor(string storedFileName)
    {
        if (!StoredFileNamePattern().IsMatch(storedFileName))
            throw new ArgumentException($"Ungültiger interner Dateiname: {storedFileName}");
        return Path.Combine(Directory, storedFileName);
    }

    [GeneratedRegex(@"^[0-9a-f]{32}\.(pdf)$")]
    private static partial Regex StoredFileNamePattern();
}
