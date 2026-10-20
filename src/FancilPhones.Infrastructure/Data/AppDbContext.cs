using FancilPhones.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FancilPhones.Infrastructure.Data;

/// <summary>Owns only phone-management data in MNSGlobalDev.</summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string Schema = "FancilPhones";
    public DbSet<Phone> Phones => Set<Phone>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);
        model.Entity<Phone>().ToTable("Phones").HasIndex(p => p.IpAddress).IsUnique();
        model.Entity<Contact>().ToTable("Contacts").HasIndex(c => c.DisplayName);
        model.Entity<SyncRun>().ToTable("SyncRuns")
            .HasOne(r => r.Phone).WithMany().HasForeignKey(r => r.PhoneId)
            .OnDelete(DeleteBehavior.Cascade);

        // SQL Server datetime2 has no timezone; all persisted timestamps are UTC.
        var utc = new ValueConverter<DateTime, DateTime>(
            value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
            value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        foreach (var entity in model.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                property.SetValueConverter(utc);
    }
}
