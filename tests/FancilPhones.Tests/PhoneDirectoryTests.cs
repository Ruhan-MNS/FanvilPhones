using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FancilPhones.Tests;

public sealed class PhoneDirectoryTests : IAsyncLifetime
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private Factory factory = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    [Fact]
    public async Task ViewersCannotMutatePhoneOrContactData()
    {
        var directory = new PhoneDirectory(factory, new Access(false));
        Assert.Empty(await directory.GetPhonesAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.SavePhoneAsync(new Phone()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.DeletePhoneAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.SetPhoneEnabledAsync(1, false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ImportPhonesAsync([], true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.SaveContactAsync(new Contact()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.DeleteContactAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => directory.ReplaceContactsAsync([]));
    }

    [Fact]
    public async Task ViewersCannotRunSyncOrPushSipSettings()
    {
        var sync = new PhoneSyncService(factory, NullLogger<PhoneSyncService>.Instance, new Access(false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.SyncAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.SyncOneAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.PushSipDisplayNameAsync(1));
    }

    [Fact]
    public async Task SavingPhoneSettingsPreservesSyncHistoryAndUtcTimestamps()
    {
        var time = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = factory.CreateDbContext())
        {
            db.Phones.Add(new Phone { Name = "Original", IpAddress = "192.0.2.1", LastSyncedAt = time, LastSyncStatus = "Success" });
            await db.SaveChangesAsync();
        }
        var directory = new PhoneDirectory(factory, new Access(true));
        var phone = Assert.Single(await directory.GetPhonesAsync());
        await directory.SavePhoneAsync(new Phone { Id = phone.Id, Name = "Updated", IpAddress = phone.IpAddress });
        var updated = Assert.Single(await directory.GetPhonesAsync());
        Assert.Equal("Updated", updated.Name);
        Assert.Equal("Success", updated.LastSyncStatus);
        Assert.Equal(time, updated.LastSyncedAt);
        Assert.Equal(DateTimeKind.Utc, updated.LastSyncedAt!.Value.Kind);
    }

    [Fact]
    public async Task FailedReplacementRollsBackPhonesAndCascadedLogs()
    {
        await using (var db = factory.CreateDbContext())
        {
            var phone = new Phone { Name = "Original", IpAddress = "192.0.2.1" };
            db.SyncRuns.Add(new SyncRun { Phone = phone, Status = "Success" });
            await db.SaveChangesAsync();
        }
        var directory = new PhoneDirectory(factory, new Access(true));
        await Assert.ThrowsAsync<DbUpdateException>(() => directory.ImportPhonesAsync(
            [new Phone { Name = null!, IpAddress = "192.0.2.2" }], true));
        Assert.Equal("Original", Assert.Single(await directory.GetPhonesAsync()).Name);
        Assert.Single(await directory.GetHistoryAsync(50));
    }

    [Fact]
    public async Task MergeByIpUpdatesExistingAndAddsNewPhones()
    {
        var directory = new PhoneDirectory(factory, new Access(true));
        await directory.SavePhoneAsync(new Phone { Name = "Original", IpAddress = "192.0.2.1" });
        var result = await directory.ImportPhonesAsync([
            new Phone { Name = "Updated", IpAddress = "192.0.2.1", SipExtension = "144" },
            new Phone { Name = "New", IpAddress = "192.0.2.2" }], false);
        Assert.Equal(new PhoneImportResult(1, 1), result);
        Assert.Equal(2, (await directory.GetPhonesAsync()).Count);
        Assert.Equal("144", (await directory.GetPhonesAsync()).Single(p => p.Name == "Updated").SipExtension);
    }

    [Fact]
    public void SqlServerModelContainsOnlyOwnedTablesAndAuthHasNoLocalPasswordFlag()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=MNSGlobalDev;Integrated Security=True").Options);
        Assert.Equal(new[] { "Contacts", "Phones", "SyncRuns" }, db.Model.GetEntityTypes().Select(e => e.GetTableName()!).Order().ToArray());
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal(AppDbContext.Schema, e.GetSchema()));
        Assert.Null(typeof(AppUser).GetProperty("MustChangePassword"));
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class Access(bool administrator) : IUserAccess
    {
        public Task RequireAuthenticatedAsync() => Task.CompletedTask;
        public Task RequireAdministratorAsync() => administrator ? Task.CompletedTask
            : Task.FromException(new UnauthorizedAccessException());
    }
}
