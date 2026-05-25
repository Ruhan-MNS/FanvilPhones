using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Data;

public class AppDbContext : IdentityDbContext<AppUser, IdentityRole, string>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Phone> Phones => Set<Phone>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<SyncRun> SyncRuns => Set<SyncRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // configures Identity tables
        b.Entity<Phone>().HasIndex(p => p.IpAddress).IsUnique();
        b.Entity<Contact>().HasIndex(c => c.DisplayName);
        b.Entity<SyncRun>()
            .HasOne(r => r.Phone)
            .WithMany()
            .HasForeignKey(r => r.PhoneId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
