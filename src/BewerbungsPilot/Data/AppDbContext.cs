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
