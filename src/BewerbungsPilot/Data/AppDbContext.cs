using BewerbungsPilot.Logic;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BewerbungsPilot.Data;

// The DbContext is EF Core's "gateway" to the database.
// Each DbSet<T> represents one table. The user accounts (ASP.NET Core Identity, tables
// AspNetUsers etc.) live in the same database.
//
// Data isolation between users happens HERE, in two layers:
// 1. Global query filters: every query on applications, history, documents and interviews
//    only returns rows of the current user. No user → no rows ("fail closed").
// 2. Ownership guard in SaveChanges: every insert, update and delete is checked against the
//    database before it is written – also for child rows that were added via a bare
//    JobApplicationId, which no query filter would ever see.
// Limitations and the few allowed exceptions: docs/security.md.
public class AppDbContext : IdentityDbContext<IdentityUser>
{
    private readonly CurrentUser? _currentUser;

    public AppDbContext(DbContextOptions<AppDbContext> options, CurrentUser? currentUser = null) : base(options)
    {
        _currentUser = currentUser;
    }

    // Must stay a property of the context INSTANCE: EF Core re-reads it for every query.
    // (A captured local variable would be frozen into the cached model for all users.)
    public string? CurrentUserId => _currentUser?.UserId;

    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<ApplicationDocument> Documents => Set<ApplicationDocument>();
    public DbSet<Interview> Interviews => Set<Interview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder); // Identity tables

        modelBuilder.Entity<JobApplication>(entity =>
        {
            entity.Property(a => a.CompanyName).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Position).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Location).HasMaxLength(200);
            entity.Property(a => a.JobUrl).HasMaxLength(2000);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(30);
            // Existing rows get "Unknown" when the column is added (see migration AddMapLocation).
            entity.Property(a => a.LocationPrecision).HasConversion<string>().HasMaxLength(20);
            entity.Property(a => a.MapLabel).HasMaxLength(300);

            // Owner. Restrict: a user account cannot be deleted while it still owns applications.
            entity.Property(a => a.OwnerId).HasMaxLength(450);
            entity.HasOne<IdentityUser>()
                  .WithMany()
                  .HasForeignKey(a => a.OwnerId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(a => a.OwnerId);
            entity.HasQueryFilter(a => a.OwnerId != null && a.OwnerId == CurrentUserId);

            // 1:n relationship. Deleting an application deletes its history too.
            entity.HasMany(a => a.Events)
                  .WithOne(e => e.JobApplication)
                  .HasForeignKey(e => e.JobApplicationId)
                  .OnDelete(DeleteBehavior.Cascade);

            // 1:n relationship. Deleting an application deletes its document rows;
            // ApplicationService.DeleteAsync removes the files themselves.
            entity.HasMany(a => a.Documents)
                  .WithOne(d => d.JobApplication)
                  .HasForeignKey(d => d.JobApplicationId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Interview>(entity =>
        {
            // Deleting an application deletes its interviews.
            entity.HasOne(i => i.JobApplication)
                  .WithMany(a => a.Interviews)
                  .HasForeignKey(i => i.JobApplicationId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(i => i.JobApplication!.OwnerId != null && i.JobApplication.OwnerId == CurrentUserId);

            // Local wall-clock time: overrides the "everything is UTC" convention below.
            entity.Property(i => i.ScheduledAt).HasConversion(
                value => value,
                value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
            entity.HasIndex(i => i.ScheduledAt);

            entity.Property(i => i.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(i => i.Location).HasMaxLength(500);
            entity.Property(i => i.ContactPerson).HasMaxLength(200);
            entity.Property(i => i.Notes).HasMaxLength(4000);
        });

        modelBuilder.Entity<ApplicationDocument>(entity =>
        {
            entity.ToTable("Documents");
            entity.HasQueryFilter(d => d.JobApplication!.OwnerId != null && d.JobApplication.OwnerId == CurrentUserId);
            entity.Property(d => d.Category).HasConversion<string>().HasMaxLength(30);
            entity.Property(d => d.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(d => d.StoredFileName).HasMaxLength(100).IsRequired();
            entity.HasIndex(d => d.StoredFileName).IsUnique();
            entity.Property(d => d.ContentType).HasMaxLength(100);
            entity.Property(d => d.Sha256).HasMaxLength(64);
        });

        modelBuilder.Entity<ApplicationEvent>(entity =>
        {
            entity.HasQueryFilter(e => e.JobApplication!.OwnerId != null && e.JobApplication.OwnerId == CurrentUserId);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.Description).HasMaxLength(500);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite stores no time zone. We always save UTC and mark values as UTC
        // when reading them back, so the browser can convert them to local time.
        // Exception: Interview.ScheduledAt (local time, configured above).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
        {
        }
    }

    // ---------- Ownership guard ----------

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceOwnershipAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await EnforceOwnershipAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Throws OwnershipViolationException BEFORE anything is written if a pending change touches
    // data that does not belong to the current user. The checks use the filtered sets, i.e.
    // what is really stored in the database – not the (possibly forged) values in the change tracker.
    private async Task EnforceOwnershipAsync(CancellationToken cancellationToken)
    {
        var changes = ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => e.Entity is JobApplication or ApplicationEvent or ApplicationDocument or Interview)
            .ToList();
        if (changes.Count == 0)
            return;
        if (CurrentUserId is null)
            throw new OwnershipViolationException("Änderung an Bewerbungsdaten ohne angemeldeten Benutzer.");

        var applicationIds = new HashSet<int>(); // must exist AND belong to the current user
        var childIds = new Dictionary<Type, HashSet<int>>();

        foreach (var entry in changes)
        {
            if (entry.Entity is JobApplication application)
            {
                if (entry.State == EntityState.Added)
                    application.OwnerId ??= CurrentUserId;
                if (application.OwnerId != CurrentUserId)
                    throw new OwnershipViolationException($"Bewerbung {application.Id}: fremder Besitzer.");
                if (entry.State != EntityState.Added)
                    applicationIds.Add(application.Id);
                continue;
            }

            var foreignKey = entry.Property(nameof(ApplicationEvent.JobApplicationId));
            if (entry.State == EntityState.Added)
            {
                // A temporary key means: the parent is a new application in this same save (checked above).
                if (!foreignKey.IsTemporary)
                    applicationIds.Add((int)foreignKey.CurrentValue!);
                continue;
            }

            if (entry.State == EntityState.Modified)
            {
                if (foreignKey.IsModified && !Equals(foreignKey.OriginalValue, foreignKey.CurrentValue))
                    throw new OwnershipViolationException($"{entry.Metadata.ClrType.Name}: Zuordnung zu einer anderen Bewerbung ist nicht erlaubt.");
                applicationIds.Add((int)foreignKey.CurrentValue!);
            }

            // Modified or deleted: the stored row itself must belong to the current user.
            var type = entry.Metadata.ClrType;
            if (!childIds.TryGetValue(type, out var ids))
                childIds[type] = ids = new HashSet<int>();
            ids.Add((int)entry.Property("Id").CurrentValue!);
        }

        if (applicationIds.Count > 0)
        {
            var owned = await Applications.Where(a => applicationIds.Contains(a.Id)).CountAsync(cancellationToken);
            if (owned != applicationIds.Count)
                throw new OwnershipViolationException("Bewerbung gehört nicht zum angemeldeten Benutzer.");
        }

        foreach (var (type, ids) in childIds)
        {
            var owned = type == typeof(ApplicationEvent) ? await ApplicationEvents.CountAsync(e => ids.Contains(e.Id), cancellationToken)
                : type == typeof(ApplicationDocument) ? await Documents.CountAsync(d => ids.Contains(d.Id), cancellationToken)
                : await Interviews.CountAsync(i => ids.Contains(i.Id), cancellationToken);
            if (owned != ids.Count)
                throw new OwnershipViolationException($"{type.Name}: Datensatz gehört nicht zum angemeldeten Benutzer.");
        }
    }
}

// Raised by the ownership guard. The API answers 404 (see ApiFilters.cs), exactly like for an
// id that does not exist, so other users' ids cannot be discovered this way.
public class OwnershipViolationException : Exception
{
    public OwnershipViolationException(string message) : base(message)
    {
    }
}
