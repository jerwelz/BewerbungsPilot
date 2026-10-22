using System.Security.Cryptography;
using BewerbungsPilot.Data;
using BewerbungsPilot.Logic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BewerbungsPilot.Tests;

// The fail-safe "migrate" command (MigrationSafety.cs) on a populated database file in the
// state BEFORE M9 (migration AddMapLocation), with filled foreign-key relationships.
public sealed class MigrationSafetyTests : IDisposable
{
    private const string LastM8Migration = "20261022151700_AddMapLocation";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BewerbungsPilot.Tests", "migration-" + Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly DocumentStorage _storage;

    public MigrationSafetyTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "bewerbungspilot.db");
        _storage = new DocumentStorage(Path.Combine(_directory, "documents"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_databasePath}").Options);

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection($"Data Source={_databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; " + sql;
        command.ExecuteNonQuery();
    }

    private readonly List<(string Name, string Sha256)> _files = [];

    // Three applications: #1 with history, 2 documents, 2 interviews, deadline and location;
    // #2 with history and 1 document; #3 without children. Plus one document whose file is missing.
    private async Task CreateM8DatabaseAsync()
    {
        using (var db = CreateContext())
            db.GetService<IMigrator>().Migrate(LastM8Migration);

        string Document(string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.4\n" + text);
            var name = _storage.SaveAsync(bytes, ".pdf").GetAwaiter().GetResult();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            _files.Add((name, hash));
            return $"'{name}', '{hash}'";
        }

        using var connection = OpenRaw();
        const string times = "'2026-09-01 10:00:00', '2026-09-02 10:00:00', '2026-09-03 10:00:00'";
        Execute(connection, $"""
            INSERT INTO Applications (Id, CompanyName, Position, Location, Status, Notes, Deadline, LocationPrecision, MapLatitude, MapLongitude, MapLabel, CreatedAt, UpdatedAt, StatusChangedAt)
            VALUES (1, 'Nordlicht GmbH', 'Azubi', 'Hamburg', 'Vorstellungsgespraech', 'Notiz äöü', '2026-11-05', 'Exact', 53.5446, 9.9661, 'Hafenstraße 12, Hamburg', {times}),
                   (2, 'Hafen Logistik AG', 'Azubi', 'Kiel', 'Beworben', NULL, NULL, 'Remote', NULL, NULL, 'Remote', {times}),
                   (3, 'Leer GmbH', 'Azubi', NULL, 'Entwurf', NULL, NULL, 'Unknown', NULL, NULL, NULL, {times});
            INSERT INTO ApplicationEvents (JobApplicationId, Type, FromStatus, ToStatus, Description, OccurredAt) VALUES
                (1, 'Created', NULL, 'Beworben', 'Bewerbung angelegt', '2026-09-01 10:00:00'),
                (1, 'StatusChanged', 'Beworben', 'Vorstellungsgespraech', 'Status geändert', '2026-09-10 10:00:00'),
                (1, 'DocumentAdded', NULL, NULL, 'Dokument hinzugefügt', '2026-09-11 10:00:00'),
                (2, 'Created', NULL, 'Beworben', 'Bewerbung angelegt', '2026-09-02 10:00:00');
            INSERT INTO Documents (JobApplicationId, Category, OriginalFileName, StoredFileName, Sha256, ContentType, SizeBytes, UploadedAt) VALUES
                (1, 'Lebenslauf', 'Lebenslauf.pdf', {Document("Lebenslauf")}, 'application/pdf', 20, '2026-09-11 10:00:00'),
                (1, 'Anschreiben', 'Anschreiben.pdf', {Document("Anschreiben")}, 'application/pdf', 21, '2026-09-11 11:00:00'),
                (2, 'Zeugnis', 'Zeugnis.pdf', {Document("Zeugnis")}, 'application/pdf', 17, '2026-09-12 10:00:00'),
                (2, 'Sonstiges', 'Verloren.pdf', '{new string('a', 32)}.pdf', '{new string('0', 64)}', 'application/pdf', 1, '2026-09-12 11:00:00');
            INSERT INTO Interviews (JobApplicationId, ScheduledAt, Type, Status, Location, ContactPerson, Notes, CreatedAt, UpdatedAt) VALUES
                (1, '2026-09-20 14:30:00', 'Video', 'Abgeschlossen', 'https://meet.example.org', 'Frau Berg', NULL, '2026-09-10 10:00:00', '2026-09-10 10:00:00'),
                (1, '2026-10-28 10:00:00', 'VorOrt', 'Geplant', 'Hafenstraße 12', NULL, 'zweites Gespräch', '2026-09-21 10:00:00', '2026-09-21 10:00:00');
            """);
        await Task.CompletedTask;
    }

    private string BackupRoot => Path.Combine(_directory, "backups");

    private string? AppliedMigration()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    // ---------- Success ----------

    [Fact]
    public async Task Migrate_PopulatedDatabase_PreservesEveryRowValueAndDocument()
    {
        await CreateM8DatabaseAsync();
        using var db = CreateContext();
        Assert.Equal(MigrationSafety.State.MigrationRequired, MigrationSafety.GetState(db));

        var log = new StringWriter();
        var report = await MigrationSafety.MigrateWithBackupAsync(db, _storage, BackupRoot, log);

        // Counts and content of every pre-existing column are identical (checked by the command itself) …
        foreach (var table in new[] { "Applications", "ApplicationEvents", "Documents", "Interviews" })
        {
            Assert.Equal(report.Before!.RowCounts[table], report.After!.RowCounts[table]);
            Assert.Equal(report.Before.ContentHashes[table], report.After.ContentHashes[table]);
        }
        Assert.Equal((3L, 4L, 4L, 2L), (report.After!.RowCounts["Applications"], report.After.RowCounts["ApplicationEvents"],
            report.After.RowCounts["Documents"], report.After.RowCounts["Interviews"]));
        Assert.Equal((1L, 2L), (report.After.Deadlines, report.After.ConfirmedLocations));

        // … and independently: no child row lost by the table rebuild, values unchanged, nobody owns anything.
        using (var connection = OpenRaw())
        {
            Assert.Equal(3, Scalar(connection, "SELECT COUNT(*) FROM ApplicationEvents WHERE JobApplicationId = 1"));
            Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM Documents WHERE JobApplicationId = 1"));
            Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM Interviews WHERE JobApplicationId = 1"));
            Assert.Equal(2, Scalar(connection, "SELECT COUNT(*) FROM Documents WHERE JobApplicationId = 2"));
            Assert.Equal(0, Scalar(connection, "SELECT COUNT(*) FROM Applications WHERE OwnerId IS NOT NULL"));
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check";
            using (var reader = check.ExecuteReader())
                Assert.False(reader.Read());
        }
        var legacy = await db.Applications.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == 1);
        Assert.Equal(("Notiz äöü", new DateOnly(2026, 11, 5), 53.5446, "Hafenstraße 12, Hamburg", LocationPrecision.Exact),
            (legacy.Notes, legacy.Deadline, legacy.MapLatitude, legacy.MapLabel, legacy.LocationPrecision));
        Assert.Equal(MigrationSafety.State.UpToDate, MigrationSafety.GetState(db));

        // Documents untouched in place; the missing file was reported, not "repaired".
        foreach (var (name, sha) in _files)
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(_storage.Directory, name)))));
        Assert.Contains(report.Documents, d => d.Status == "Datei fehlt");
        Assert.Contains("WARNUNG", log.ToString());

        // The backup: old schema, same data, verified document copies, manifest.
        var backupDb = Path.Combine(report.BackupDirectory!, "bewerbungspilot.db");
        using (var backup = new SqliteConnection($"Data Source={backupDb};Mode=ReadOnly;Pooling=False"))
        {
            backup.Open();
            Assert.Equal(3, Scalar(backup, "SELECT COUNT(*) FROM Applications"));
            Assert.Equal(0, Scalar(backup, "SELECT COUNT(*) FROM pragma_table_info('Applications') WHERE name = 'OwnerId'"));
            using var command = backup.CreateCommand();
            command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            Assert.Equal(LastM8Migration, command.ExecuteScalar());
        }
        foreach (var (name, sha) in _files)
            Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(report.BackupDirectory!, "documents", name)))));
        Assert.True(File.Exists(Path.Combine(report.BackupDirectory!, "manifest.json")));
    }

    // ---------- Startup decision ----------

    [Fact]
    public void GetState_NewEmptyDatabase_IsMigratedAutomatically()
    {
        using var db = CreateContext();
        Assert.Equal(MigrationSafety.State.NewDatabase, MigrationSafety.GetState(db));
    }

    // ---------- Failures before the migration: database untouched ----------

    [Fact]
    public async Task BackupCannotBeCreated_AbortsWithoutMigrating()
    {
        await CreateM8DatabaseAsync();
        File.WriteAllText(BackupRoot, "a file where the backup folder should be");
        var migrated = false;
        using var db = CreateContext();

        await Assert.ThrowsAsync<MigrationSafetyException>(() =>
            MigrationSafety.MigrateWithBackupAsync(db, _storage, BackupRoot, TextWriter.Null, () => { migrated = true; return Task.CompletedTask; }));

        Assert.False(migrated);
        Assert.Equal(LastM8Migration, AppliedMigration());
    }

    [Fact]
    public async Task ValidateBackup_RejectsCorruptedAndDifferentBackups()
    {
        await CreateM8DatabaseAsync();
        DatabaseSnapshot snapshot;
        using (var connection = OpenRaw())
        {
            snapshot = MigrationSafety.TakeSnapshot(connection);
            // WAL mode: without a checkpoint, recent rows are only in the -wal file and a plain
            // file copy would miss them (the reason why the real backup uses VACUUM INTO).
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        SqliteConnection.ClearAllPools();

        var copy = Path.Combine(_directory, "copy.db");
        File.Copy(_databasePath, copy);
        MigrationSafety.ValidateBackup(copy, snapshot); // identical copy: valid

        // Different content: one history row less.
        using (var connection = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            connection.Open();
            Execute(connection, "DELETE FROM ApplicationEvents WHERE Id = 2");
        }
        Assert.Throws<MigrationSafetyException>(() => MigrationSafety.ValidateBackup(copy, snapshot));

        // Corrupted file: overwrite everything after the header page.
        var corrupt = Path.Combine(_directory, "corrupt.db");
        File.Copy(_databasePath, corrupt);
        var bytes = File.ReadAllBytes(corrupt);
        for (var i = 4096; i < bytes.Length; i++)
            bytes[i] = 0xA5;
        File.WriteAllBytes(corrupt, bytes);
        Assert.Throws<MigrationSafetyException>(() => MigrationSafety.ValidateBackup(corrupt, snapshot));

        Assert.Throws<MigrationSafetyException>(() => MigrationSafety.ValidateBackup(Path.Combine(_directory, "missing.db"), snapshot));
    }

    // ---------- Failures after the migration: detected, restore instructions given ----------

    [Fact]
    public async Task LostRowDuringMigration_IsDetected()
    {
        await CreateM8DatabaseAsync();
        using var db = CreateContext();
        var error = await Assert.ThrowsAsync<MigrationSafetyException>(() =>
            MigrationSafety.MigrateWithBackupAsync(db, _storage, BackupRoot, TextWriter.Null, async () =>
            {
                await db.Database.MigrateAsync();
                using var connection = OpenRaw();
                Execute(connection, "DELETE FROM Interviews WHERE Id = 2"); // simulated data loss
            }));
        Assert.Contains("Interviews", error.Message);
        Assert.Contains("Wiederherstellen", error.Message);
    }

    [Fact]
    public async Task ChangedDocumentDuringMigration_IsDetected()
    {
        await CreateM8DatabaseAsync();
        using var db = CreateContext();
        var error = await Assert.ThrowsAsync<MigrationSafetyException>(() =>
            MigrationSafety.MigrateWithBackupAsync(db, _storage, BackupRoot, TextWriter.Null, async () =>
            {
                await db.Database.MigrateAsync();
                File.WriteAllText(Path.Combine(_storage.Directory, _files[0].Name), "%PDF-1.4 verändert");
            }));
        Assert.Contains("Dokument", error.Message);
        Assert.Contains("Wiederherstellen", error.Message);
    }

    [Fact]
    public async Task AlreadyCurrentDatabase_NeedsNoMigration()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();
        var report = await MigrationSafety.MigrateWithBackupAsync(db, _storage, BackupRoot, TextWriter.Null);
        Assert.Null(report.BackupDirectory);
        Assert.False(Directory.Exists(BackupRoot));
    }
}
