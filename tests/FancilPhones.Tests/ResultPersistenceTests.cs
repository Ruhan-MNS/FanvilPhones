using System.Data.Common;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FancilPhones.Tests;

public sealed partial class AsyncOperationTests
{
    [Theory]
    [InlineData("Sync")]
    [InlineData("Push")]
    public async Task SlowResultPersistenceHasItsOwnDeadlineBeyondHandsetCleanup(string action)
    {
        var interceptor = new BeforePhoneMetadataUpdate(async ct => await Task.Delay(1500, ct));
        UsePersistenceInterceptor(interceptor);
        var uploads = 0;
        var clients = new ControlledFanvilFactory
        {
            Upload = (_, _) => { uploads++; return Task.FromResult(new FanvilResponse { StatusCode = 200 }); }
        };
        var device = new SipDevice();
        var service = CreatePersistenceService(action == "Push" ? device : clients);

        if (action == "Push")
            Assert.True((await service.PushSipDisplayNameAsync(1)).ok);
        else
            Assert.True((await service.SyncOneAsync(1)).Success);

        await using var db = factory.CreateDbContext();
        var run = await db.SyncRuns.SingleAsync();
        var phone = await db.Phones.SingleAsync(p => p.Id == 1);
        Assert.Equal("Success", run.Status);
        Assert.Equal(action, run.Action);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(run.FinishedAt, phone.LastSyncedAt);
        Assert.Equal(run.Status, phone.LastSyncStatus);
        Assert.Equal(1, interceptor.Calls);
        Assert.Equal(action == "Push" ? 0 : 1, uploads);
        Assert.Equal(action == "Push" ? 1 : 0, device.Applies.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistenceFailureRollsBackHistoryAndLogsTheDeadlineWithoutReplayingTheHandset(bool deadlineExpired)
    {
        var failure = new InvalidOperationException("Synthetic persistence failure");
        UsePersistenceInterceptor(new BeforePhoneMetadataUpdate(async ct =>
        {
            if (deadlineExpired) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw failure;
        }));
        var uploads = 0;
        var clients = new ControlledFanvilFactory
        {
            Upload = (_, _) => { uploads++; return Task.FromResult(new FanvilResponse { StatusCode = 200 }); }
        };
        using var provider = new RecordingLoggerProvider();
        using var loggers = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var service = CreatePersistenceService(clients, loggers.CreateLogger<PhoneSyncService>(), persistenceTimeout: 1);

        if (deadlineExpired)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SyncOneAsync(1).WaitAsync(TimeSpan.FromSeconds(5)));
        else
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.SyncOneAsync(1)));

        Assert.Equal(1, uploads);
        Assert.Equal(1, clients.Created);
        Assert.Equal(1, clients.LoggedOut);
        Assert.Equal(1, clients.Disposed);
        await using var db = factory.CreateDbContext();
        var run = await db.SyncRuns.SingleAsync();
        Assert.Equal("Running", run.Status);
        Assert.Null(run.FinishedAt);
        Assert.Null((await db.Phones.SingleAsync(p => p.Id == 1)).LastSyncedAt);
        var entry = Assert.Single(provider.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(entry.Properties, p => p.Key == "Status" && Equals(p.Value, "Success"));
        Assert.Contains(entry.Properties, p => p.Key == "PersistenceDeadlineExpired" && Equals(p.Value, deadlineExpired));
        Assert.Contains(entry.Properties, p => p.Key == "PersistenceTimeoutSeconds" && Equals(p.Value, 1));
        Assert.Contains(entry.Properties, p => p.Key == "ElapsedMilliseconds" && p.Value is long elapsed && elapsed >= 0);
        Assert.Contains("verify its state before retrying", entry.Message);
    }

    private void UsePersistenceInterceptor(DbCommandInterceptor interceptor) =>
        factory = new Factory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={database};Pooling=False").AddInterceptors(interceptor).Options);

    private PhoneSyncService CreatePersistenceService(IFanvilClientFactory clients,
        ILogger<PhoneSyncService>? logger = null, int persistenceTimeout = 5) =>
        new(factory, logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PhoneSyncService>.Instance,
            new Access(), clients, Options.Create(new PhoneOperationOptions
            {
                CleanupTimeoutSeconds = 1,
                ResultPersistenceTimeoutSeconds = persistenceTimeout
            }), new TestApplicationLifetime());

    // Delay/fail the second update, after history was updated inside the transaction.
    private sealed class BeforePhoneMetadataUpdate(Func<CancellationToken, Task> beforeUpdate) : DbCommandInterceptor
    {
        public int Calls { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"Phones\"", StringComparison.Ordinal))
            {
                Calls++;
                await beforeUpdate(cancellationToken);
            }
            return result;
        }
    }
}
