using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

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
        var directory = new PhoneDirectory(factory, new Access(false), NullLogger<PhoneDirectory>.Instance);
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
        var directory = new PhoneDirectory(factory, new Access(true), NullLogger<PhoneDirectory>.Instance);
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
        using var logs = new RecordingLoggerProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var directory = new PhoneDirectory(factory, new Access(true), logFactory.CreateLogger<PhoneDirectory>());
        await Assert.ThrowsAsync<DbUpdateException>(() => directory.ImportPhonesAsync(
            [new Phone { Name = null!, IpAddress = "192.0.2.2" }], true));
        Assert.Equal("Original", Assert.Single(await directory.GetPhonesAsync()).Name);
        Assert.Single(await directory.GetHistoryAsync(50));
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("import committed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationLogsContainRecordIdsWithoutCredentialsOrContactDetails()
    {
        using var logs = new RecordingLoggerProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var directory = new PhoneDirectory(factory, new Access(true), logFactory.CreateLogger<PhoneDirectory>());
        var phone = new Phone { Name = "Private handset", IpAddress = "192.0.2.7", Username = "secret-user", Password = "secret-password" };
        var contact = new Contact { DisplayName = "Private employee", MobileNumber = "+27123456789" };
        await directory.SavePhoneAsync(phone);
        await directory.SaveContactAsync(contact);

        Assert.Contains(logs.Entries, entry => entry.Properties.Any(p => p.Key == "PhoneId" && Equals(p.Value, phone.Id)));
        Assert.Contains(logs.Entries, entry => entry.Properties.Any(p => p.Key == "ContactId" && Equals(p.Value, contact.Id)));
        var text = string.Join("\n", logs.Entries.Select(entry => entry.Message + string.Join(";", entry.Properties.Select(p => p.Value))));
        foreach (var sensitive in new[] { phone.Name, phone.IpAddress, phone.Username, phone.Password, contact.DisplayName, contact.MobileNumber })
            Assert.DoesNotContain(sensitive!, text);
    }

    [Fact]
    public async Task FleetDeliveryLogsShareCorrelationScope()
    {
        using var logs = new RecordingLoggerProvider();
        using var logFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var sync = new PhoneSyncService(factory, logFactory.CreateLogger<PhoneSyncService>(), new Access(true));
        Assert.Empty(await sync.SyncAllAsync());

        var correlationIds = logs.Entries.SelectMany(entry => entry.Scope).Where(p => p.Key == "FleetSyncId").Select(p => p.Value).ToArray();
        Assert.Equal(2, correlationIds.Length);
        Assert.IsType<Guid>(correlationIds[0]);
        Assert.Equal(correlationIds[0], correlationIds[1]);
    }

    [Fact]
    public async Task MergeByIpUpdatesExistingAndAddsNewPhones()
    {
        var directory = new PhoneDirectory(factory, new Access(true), NullLogger<PhoneDirectory>.Instance);
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
