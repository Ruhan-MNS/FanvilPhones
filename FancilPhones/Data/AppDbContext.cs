using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Phone> Phones => Set<Phone>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Phone>().HasIndex(p => p.IpAddress).IsUnique();
        b.Entity<Contact>().HasIndex(c => c.DisplayName);
        b.Entity<SyncRun>()
            .HasOne(r => r.Phone)
            .WithMany()
            .HasForeignKey(r => r.PhoneId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
