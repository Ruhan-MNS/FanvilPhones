using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

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
        var sync = new PhoneSyncService(factory, NullLogger<PhoneSyncService>.Instance, new Access(false), new FanvilClientFactory(), Options.Create(new PhoneOperationOptions()), new TestApplicationLifetime());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.SyncAllAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.SyncOneAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.PushSipDisplayNameAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.ReadSipSettingsAsync(1, 1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sync.PushSipSettingsAsync(new(1, 1, "fingerprint", "Registered", new()), new()));
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
        var sync = new PhoneSyncService(factory, logFactory.CreateLogger<PhoneSyncService>(), new Access(true), new FanvilClientFactory(), Options.Create(new PhoneOperationOptions()), new TestApplicationLifetime());
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationAfterBulkDeleteRollsBackTheEntireReplacement(bool replaceContacts)
    {
        await using (var db = factory.CreateDbContext())
        {
            db.Contacts.Add(new Contact { DisplayName = "Original contact" });
            db.SyncRuns.Add(new SyncRun { Phone = new Phone { Name = "Original phone", IpAddress = "192.0.2.8" }, Status = "Success" });
            await db.SaveChangesAsync();
        }
        using var cancellation = new CancellationTokenSource();
        var cancellingFactory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection)
            .AddInterceptors(new CancelAfterDelete(cancellation)).Options);
        var directory = new PhoneDirectory(cancellingFactory, new Access(true), NullLogger<PhoneDirectory>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replaceContacts
            ? directory.ReplaceContactsAsync([new Contact { DisplayName = "Replacement" }], cancellation.Token)
            : directory.ImportPhonesAsync([new Phone { Name = "Replacement", IpAddress = "192.0.2.9" }], true, cancellation.Token));
        await using var verification = factory.CreateDbContext();
        Assert.Equal("Original contact", (await verification.Contacts.SingleAsync()).DisplayName);
        Assert.Equal("Original phone", (await verification.Phones.SingleAsync()).Name);
        Assert.Single(await verification.SyncRuns.ToListAsync());
    }

    [Fact]
    public async Task HistoryFiltersBeforeLimitingAndExcludesHandsetCredentials()
    {
        await using (var db = factory.CreateDbContext())
        {
            var phone = new Phone { Name = "Phone", IpAddress = "192.0.2.10", Password = "do-not-load" };
            db.SyncRuns.Add(new SyncRun { Phone = phone, Action = "Sync", StartedAt = DateTime.UtcNow.AddDays(-1) });
            db.SyncRuns.AddRange(Enumerable.Range(0, 501).Select(_ => new SyncRun { Phone = phone, Action = "Probe" }));
            await db.SaveChangesAsync();
        }
        var directory = new PhoneDirectory(factory, new Access(true), NullLogger<PhoneDirectory>.Instance);
        var run = Assert.Single(await directory.GetHistoryAsync(50, action: "Sync"));
        Assert.Equal("Sync", run.Action);
        Assert.Equal("", run.Phone!.Password);
        Assert.Equal("", run.Phone.Username);
        var summary = await directory.GetSummaryAsync();
        Assert.Equal(1, summary.PhoneCount);
        Assert.Equal(1, summary.EnabledPhoneCount);
    }

    private sealed class CancelAfterDelete(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public void SqlServerModelContainsOnlyOwnedTablesAndAuthHasNoLocalPasswordFlag()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=MNSGlobalDev;Integrated Security=True").Options);
        Assert.Equal(new[] { "Contacts", "Phones", "SwitchboardDeployments", "SwitchboardMembers", "SwitchboardOverrides", "SwitchboardProfiles", "SyncRuns" }, db.Model.GetEntityTypes().Select(e => e.GetTableName()!).Order().ToArray());
        Assert.All(db.Model.GetEntityTypes(), e => Assert.Equal(AppDbContext.Schema, e.GetSchema()));
        Assert.Null(typeof(AppUser).GetProperty("MustChangePassword"));
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class Access(bool administrator) : IUserAccess
    {
        public Task RequireAuthenticatedAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RequireAdministratorAsync(CancellationToken ct = default) => administrator ? Task.CompletedTask
            : Task.FromException(new UnauthorizedAccessException());
    }
}
