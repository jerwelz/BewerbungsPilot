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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<JobApplication>(entity =>
        {
            entity.Property(a => a.CompanyName).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Position).HasMaxLength(200).IsRequired();
            entity.Property(a => a.Location).HasMaxLength(200);
            entity.Property(a => a.JobUrl).HasMaxLength(2000);
            entity.Property(a => a.Status).HasConversion<string>().HasMaxLength(30);

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
