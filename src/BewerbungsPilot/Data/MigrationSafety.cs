using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BewerbungsPilot.Logic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BewerbungsPilot.Data;

// Protects existing data when the database schema changes.
//
// - New, empty database: migrations are applied automatically at startup (as before).
// - Existing database with pending migrations: the app refuses to start. The operator runs
//   "dotnet run -- migrate", which calls MigrateWithBackupAsync:
//     1. check the database (integrity, foreign keys) and take a snapshot (row counts + content hashes)
//     2. consistent backup with VACUUM INTO
//     3. validate the backup (opens, integrity ok, same snapshot)
//     4. copy every document file into the backup folder and verify its SHA-256
//     5. apply the migrations
//     6. verify: same rows, same values in every pre-existing column, documents unchanged
//   If step 1–4 fails, the migration is never started and the database stays untouched.
//   If step 5–6 fails, nothing is restored automatically: the error message says how to
//   restore the validated backup, so the evidence stays as it is.
//
// Uses plain SQL instead of the EF model, so the checks see every row (no query filters)
// and work with every schema version. The source database is only opened read-only.
public static class MigrationSafety
{
    public enum State { UpToDate, NewDatabase, MigrationRequired }

    public static State GetState(AppDbContext db)
    {
        if (!db.Database.GetPendingMigrations().Any())
            return State.UpToDate;
        var hasTables = db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory'")
            .AsEnumerable().First() > 0;
        return db.Database.GetAppliedMigrations().Any() || hasTables ? State.MigrationRequired : State.NewDatabase;
    }

    public static async Task<MigrationReport> MigrateWithBackupAsync(
        AppDbContext db, DocumentStorage storage, string backupRoot, TextWriter log, Func<Task>? migrate = null)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        var databasePath = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        if (pending.Count == 0)
        {
            log.WriteLine("Die Datenbank ist aktuell – keine Migration nötig.");
            return new MigrationReport(databasePath, null, [], null, null, []);
        }
        log.WriteLine($"Datenbank: {databasePath}");
        log.WriteLine($"Ausstehende Migrationen: {string.Join(", ", pending)}");

        // 1. Check and snapshot (read-only).
        DatabaseSnapshot before;
        using (var connection = Open(databasePath, readOnly: true))
        {
            CheckIntegrity(connection, "Quelldatenbank");
            before = TakeSnapshot(connection);
        }
        var documents = BuildDocumentManifest(before, databasePath, storage);
        Print(log, "Vorher", before);
        log.WriteLine($"Dokumente: {documents.Count(d => d.IsOk)} von {documents.Count} Dateien vorhanden und unverändert.");
        foreach (var problem in documents.Where(d => !d.IsOk))
            log.WriteLine($"  WARNUNG: Dokument {problem.Id} ({problem.StoredFileName}): {problem.Status} – war schon vor der Migration so.");

        // 2. Consistent backup into a new folder.
        var backupDirectory = Path.Combine(backupRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-pre-{pending[^1]}");
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
            throw new MigrationSafetyException($"Der Sicherungsordner existiert bereits: {backupDirectory}. Abbruch – die Datenbank wurde nicht verändert.");
        string backupPath;
        try
        {
            Directory.CreateDirectory(backupDirectory);
            backupPath = Path.Combine(backupDirectory, "bewerbungspilot.db");
            using var connection = Open(databasePath, readOnly: true);
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM INTO $path";
            vacuum.Parameters.AddWithValue("$path", backupPath);
            vacuum.ExecuteNonQuery();
        }
        catch (Exception exception) when (exception is not MigrationSafetyException)
        {
            throw new MigrationSafetyException($"Die Sicherung konnte nicht erstellt werden ({exception.Message}). Abbruch – die Datenbank wurde nicht verändert.", exception);
        }

        // 3. Validate the backup.
        ValidateBackup(backupPath, before);
        log.WriteLine($"Sicherung erstellt und geprüft: {backupPath}");

        // 4. Back up the document files and verify every copy.
        CopyDocuments(documents, storage, Path.Combine(backupDirectory, "documents"));
        await File.WriteAllTextAsync(Path.Combine(backupDirectory, "manifest.json"), JsonSerializer.Serialize(new
        {
            CreatedUtc = DateTime.UtcNow,
            MigrationBefore = before.LastMigration,
            PendingMigrations = pending,
            before.RowCounts,
            before.Deadlines,
            before.ConfirmedLocations,
            Documents = documents
        }, new JsonSerializerOptions { WriteIndented = true }));
        log.WriteLine($"Dokumente gesichert und geprüft: {documents.Count(d => d.IsOk)} Dateien.");

        // 5. Migrate.
        var restoreHint = $"Wiederherstellen: App beenden, \"{backupPath}\" über \"{databasePath}\" kopieren und " +
                          $"\"{databasePath}-wal\" sowie \"{databasePath}-shm\" löschen (falls vorhanden).";
        try
        {
            await (migrate ?? (() => db.Database.MigrateAsync()))();
        }
        catch (Exception exception)
        {
            throw new MigrationSafetyException($"Die Migration ist fehlgeschlagen: {exception.Message}\n{restoreHint}", exception);
        }

        // 6. Verify the result.
        SqliteConnection.ClearAllPools(); // read the file as it is now, not through a cached connection
        DatabaseSnapshot after;
        try
        {
            using var connection = Open(databasePath, readOnly: true);
            CheckIntegrity(connection, "Migrierte Datenbank");
            after = TakeSnapshot(connection, before.Columns);
            CompareSnapshots(before, after, "Migrierte Datenbank");
            if (!before.Columns["Applications"].Contains("OwnerId") && after.Columns["Applications"].Contains("OwnerId")
                && Scalar(connection, "SELECT COUNT(*) FROM \"Applications\" WHERE \"OwnerId\" IS NOT NULL") != 0)
                throw new MigrationSafetyException("Migrierte Datenbank: Bewerbungen wurden unerwartet einem Benutzer zugeordnet.");
            foreach (var document in documents.Where(d => d.IsOk))
                if (HashStoredFile(storage, document.StoredFileName) != document.ExpectedSha256)
                    throw new MigrationSafetyException($"Dokument {document.Id} ({document.StoredFileName}) hat sich verändert oder fehlt.");
        }
        catch (Exception exception)
        {
            throw new MigrationSafetyException($"Prüfung nach der Migration fehlgeschlagen: {exception.Message}\n{restoreHint}", exception);
        }

        Print(log, "Nachher", after);
        log.WriteLine("Prüfung nach der Migration: alle Datensätze, Werte und Dokumente unverändert. ✓");
        return new MigrationReport(databasePath, backupDirectory, pending, before, after, documents);
    }

    // Opens the backup and checks that it is complete and identical to the snapshot.
    public static void ValidateBackup(string backupPath, DatabaseSnapshot expected)
    {
        try
        {
            if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
                throw new MigrationSafetyException("Die Sicherungsdatei fehlt oder ist leer.");
            using var connection = Open(backupPath, readOnly: true);
            CheckIntegrity(connection, "Sicherung");
            var actual = TakeSnapshot(connection, expected.Columns);
            if (actual.LastMigration != expected.LastMigration)
                throw new MigrationSafetyException("Sicherung: anderer Migrationsstand als die Quelldatenbank.");
            CompareSnapshots(expected, actual, "Sicherung");
        }
        catch (Exception exception) when (exception is not MigrationSafetyException)
        {
            throw new MigrationSafetyException($"Die Sicherung ist nicht lesbar ({exception.Message}). Abbruch – die Datenbank wurde nicht verändert.", exception);
        }
        catch (MigrationSafetyException exception)
        {
            throw new MigrationSafetyException($"{exception.Message} Abbruch – die Datenbank wurde nicht verändert.", exception);
        }
    }

    // ---------- Snapshot: row counts + SHA-256 over every value of every table ----------

    public static DatabaseSnapshot TakeSnapshot(SqliteConnection connection, Dictionary<string, List<string>>? columns = null)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        // With "columns" given (after migrating), only the previously existing tables and columns are hashed.
        columns ??= tables.ToDictionary(t => t, t => ColumnsOf(connection, t));
        var counts = new Dictionary<string, long>();
        var hashes = new Dictionary<string, string>();
        foreach (var (table, tableColumns) in columns)
        {
            if (!tables.Contains(table))
                throw new MigrationSafetyException($"Tabelle {table} fehlt.");
            counts[table] = Scalar(connection, $"SELECT COUNT(*) FROM \"{table}\"");
            hashes[table] = HashTable(connection, table, tableColumns);
        }

        var applicationColumns = columns.GetValueOrDefault("Applications") ?? [];
        return new DatabaseSnapshot(
            LastMigration(connection),
            counts,
            hashes,
            tables.ToDictionary(t => t, t => ColumnsOf(connection, t)),
            applicationColumns.Contains("Deadline") ? Scalar(connection, "SELECT COUNT(*) FROM \"Applications\" WHERE \"Deadline\" IS NOT NULL") : 0,
            applicationColumns.Contains("MapLatitude") ? Scalar(connection, "SELECT COUNT(*) FROM \"Applications\" WHERE \"MapLatitude\" IS NOT NULL OR \"LocationPrecision\" = 'Remote'") : 0);
    }

    private static void CompareSnapshots(DatabaseSnapshot expected, DatabaseSnapshot actual, string what)
    {
        foreach (var (table, count) in expected.RowCounts)
        {
            if (actual.RowCounts[table] != count)
                throw new MigrationSafetyException($"{what}: Tabelle {table} hat {actual.RowCounts[table]} statt {count} Datensätze.");
            if (actual.ContentHashes[table] != expected.ContentHashes[table])
                throw new MigrationSafetyException($"{what}: Inhalte der Tabelle {table} weichen ab.");
        }
    }

    private static string HashTable(SqliteConnection connection, string table, List<string> columns)
    {
        using var command = connection.CreateCommand();
        var list = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var order = string.Join(", ", Enumerable.Range(1, columns.Count));
        command.CommandText = $"SELECT {list} FROM \"{table}\" ORDER BY {order}";
        using var reader = command.ExecuteReader();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var text = reader.GetValue(i) switch
                {
                    DBNull => "N",
                    long l => "I" + l.ToString(CultureInfo.InvariantCulture),
                    double d => "R" + BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture),
                    string s => "T" + s.Length.ToString(CultureInfo.InvariantCulture) + ":" + s,
                    byte[] b => "B" + Convert.ToBase64String(b),
                    var other => "O" + Convert.ToString(other, CultureInfo.InvariantCulture)
                };
                hash.AppendData(Encoding.UTF8.GetBytes(text + "\u001f"));
            }
            hash.AppendData("\u001e"u8);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static List<string> ColumnsOf(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table.Replace("'", "''")}') ORDER BY cid";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static string? LastMigration(SqliteConnection connection)
    {
        if (Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'") == 0)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM \"__EFMigrationsHistory\" ORDER BY MigrationId DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    private static void CheckIntegrity(SqliteConnection connection, string what)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check";
            if (command.ExecuteScalar() as string != "ok")
                throw new MigrationSafetyException($"{what}: Integritätsprüfung fehlgeschlagen.");
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check";
            using var reader = command.ExecuteReader();
            if (reader.Read())
                throw new MigrationSafetyException($"{what}: verwaiste Datensätze (Fremdschlüssel) in Tabelle {reader.GetString(0)}.");
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    // Pooling off: every check really re-opens the file, and no handle stays open afterwards.
    private static SqliteConnection Open(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    // ---------- Documents ----------

    private static List<DocumentManifestEntry> BuildDocumentManifest(DatabaseSnapshot snapshot, string databasePath, DocumentStorage storage)
    {
        var entries = new List<DocumentManifestEntry>();
        if (!snapshot.Columns.ContainsKey("Documents"))
            return entries;
        using var connection = Open(databasePath, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, StoredFileName, Sha256 FROM \"Documents\" ORDER BY Id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var (id, name, expected) = (reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
            string status;
            try
            {
                var actual = HashStoredFile(storage, name);
                status = actual is null ? "Datei fehlt" : actual == expected ? "ok" : "SHA-256 weicht ab";
            }
            catch (ArgumentException)
            {
                status = "ungültiger Dateiname";
            }
            entries.Add(new DocumentManifestEntry(id, name, expected, status));
        }
        return entries;
    }

    private static void CopyDocuments(List<DocumentManifestEntry> documents, DocumentStorage storage, string targetDirectory)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (var document in documents.Where(d => d.IsOk))
            {
                var target = Path.Combine(targetDirectory, document.StoredFileName);
                using (var source = storage.OpenRead(document.StoredFileName)!)
                using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    source.CopyTo(copy);
                using var written = File.OpenRead(target);
                if (Convert.ToHexStringLower(SHA256.HashData(written)) != document.ExpectedSha256)
                    throw new MigrationSafetyException($"Die Kopie von Dokument {document.Id} ist fehlerhaft.");
            }
        }
        catch (Exception exception) when (exception is not MigrationSafetyException)
        {
            throw new MigrationSafetyException($"Die Dokumente konnten nicht gesichert werden ({exception.Message}). Abbruch – die Datenbank wurde nicht verändert.", exception);
        }
    }

    private static string? HashStoredFile(DocumentStorage storage, string storedFileName)
    {
        using var stream = storage.OpenRead(storedFileName);
        return stream is null ? null : Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void Print(TextWriter log, string label, DatabaseSnapshot snapshot)
    {
        var tables = new[] { "Applications", "ApplicationEvents", "Documents", "Interviews" }
            .Where(snapshot.RowCounts.ContainsKey)
            .Select(t => $"{t} {snapshot.RowCounts[t]}");
        log.WriteLine($"{label}: {string.Join(", ", tables)}; Fristen {snapshot.Deadlines}, bestätigte Standorte {snapshot.ConfirmedLocations}");
    }
}

public sealed record DatabaseSnapshot(
    string? LastMigration,
    Dictionary<string, long> RowCounts,
    Dictionary<string, string> ContentHashes,
    Dictionary<string, List<string>> Columns,
    long Deadlines,
    long ConfirmedLocations);

public sealed record DocumentManifestEntry(int Id, string StoredFileName, string ExpectedSha256, string Status)
{
    public bool IsOk => Status == "ok";
}

public sealed record MigrationReport(
    string DatabasePath,
    string? BackupDirectory,
    List<string> AppliedMigrations,
    DatabaseSnapshot? Before,
    DatabaseSnapshot? After,
    List<DocumentManifestEntry> Documents);

public class MigrationSafetyException : Exception
{
    public MigrationSafetyException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
