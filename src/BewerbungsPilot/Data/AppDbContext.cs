using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BewerbungsPilot.Data;

// The DbContext is EF Core's "gateway" to the database.
// Each DbSet<T> represents one table.
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<ApplicationEvent> ApplicationEvents => Set<ApplicationEvent>();
    public DbSet<ApplicationDocument> Documents => Set<ApplicationDocument>();
    public DbSet<Interview> Interviews => Set<Interview>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.Property(d => d.Category).HasConversion<string>().HasMaxLength(30);
            entity.Property(d => d.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(d => d.StoredFileName).HasMaxLength(100).IsRequired();
            entity.HasIndex(d => d.StoredFileName).IsUnique();
            entity.Property(d => d.ContentType).HasMaxLength(100);
            entity.Property(d => d.Sha256).HasMaxLength(64);
        });

        modelBuilder.Entity<ApplicationEvent>(entity =>
        {
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
}
