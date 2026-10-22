using System.Net;
using System.Net.Sockets;
using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using FancilPhones.UI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FancilPhones.Tests;

public sealed partial class AsyncOperationTests : IAsyncLifetime
{
    [Theory]
    [InlineData("login", "signing in", "No phonebook or SIP changes were requested.", 0)]
    [InlineData("delete", "clearing the phonebook", "the upload was not started", 0)]
    [InlineData("upload", "uploading the phonebook", "verify its state", 1)]
    public async Task TimeoutHistoryIdentifiesTheFailedStageWithoutReplayingTheUpload(string failure, string stage, string hint, int expectedUploads)
    {
        var uploads = 0;
        var clients = new ControlledFanvilFactory
        {
            Login = (_, _) => failure == "login" ? throw new TimeoutException("Synthetic response timeout") : Task.FromResult((true, "OK")),
            Delete = (_, _) => failure == "delete" ? throw new TimeoutException("Synthetic response timeout") : Task.FromResult(new FanvilResponse { StatusCode = 200 }),
            Upload = (_, _) => { uploads++; if (failure == "upload") throw new TimeoutException("Synthetic response timeout"); return Task.FromResult(new FanvilResponse { StatusCode = 200 }); }
        };
        var result = await CreateService(clients).SyncOneAsync(1);
        Assert.False(result.Success); Assert.Contains(stage, result.Message); Assert.Contains(hint, result.Message);
        Assert.Equal(expectedUploads, uploads); Assert.Equal(1, clients.LoggedOut);
        await using var db = factory.CreateDbContext();
        var saved = await db.SyncRuns.SingleAsync(); Assert.Equal("TimedOut", saved.Status); Assert.Equal(result.Message, saved.Message);
    }

    private readonly string database = Path.Combine(Path.GetTempPath(), $"fanvil-async-{Guid.NewGuid():N}.db");
    private Factory factory = null!;
    public async Task InitializeAsync()
    {
        factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={database};Pooling=False").Options);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        db.Phones.AddRange(Enumerable.Range(1, 6).Select(i => new Phone { Name = $"Phone {i}", IpAddress = $"192.0.2.{i}", SipDisplayName = "Test" }));
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() { File.Delete(database); return Task.CompletedTask; }

    [Theory]
    [InlineData("Sync")]
    [InlineData("Push")]
    [InlineData("Probe")]
    public async Task CallerCancellationClosesTheSessionAndPersistsTerminalHistory(string action)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new ControlledFanvilFactory
        {
            Login = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return (true, "OK"); }
        };
        var service = CreateService(clients);
        using var cancellation = new CancellationTokenSource();
        var task = action switch
        {
            "Sync" => (Task)service.SyncOneAsync(1, cancellation.Token),
            "Push" => service.PushSipDisplayNameAsync(1, cancellation.Token),
            _ => service.ProbeAsync(new Phone { Id = 1, IpAddress = "192.0.2.1" }, cancellation.Token)
        };
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var db = factory.CreateDbContext();
        var run = Assert.Single(await db.SyncRuns.ToListAsync());
        Assert.Equal("Cancelled", run.Status);
        Assert.Equal(action, run.Action);
        Assert.NotNull(run.FinishedAt);
        Assert.Equal(clients.Created, clients.Disposed);
        Assert.Equal(1, clients.LoggedOut); // Logout used its own uncancelled cleanup token.
    }

    [Fact]
    public async Task TimeoutIsRecordedAsATimeoutRatherThanCallerCancellation()
    {
        var clients = new ControlledFanvilFactory
        {
            Login = async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return (true, "OK"); }
        };
        var result = await CreateService(clients, timeoutSeconds: 1).SyncOneAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        await using var db = factory.CreateDbContext();
        Assert.Equal("TimedOut", (await db.SyncRuns.SingleAsync()).Status);
        Assert.Equal(1, clients.LoggedOut);
    }

    [Fact]
    public async Task FleetConcurrencyIsBoundedAndAllWorkersSettleAfterCancellation()
    {
        var active = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new ControlledFanvilFactory
        {
            Login = async (_, ct) =>
            {
                if (Interlocked.Increment(ref active) == 2) entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return (true, "OK"); }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        using var cancellation = new CancellationTokenSource();
        var task = CreateService(clients, concurrency: 2).SyncAllAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, clients.Created);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(2, clients.Disposed);
        await using var db = factory.CreateDbContext();
        Assert.All(await db.SyncRuns.ToListAsync(), run => Assert.Equal("Cancelled", run.Status));
    }

    [Fact]
    public async Task CompletingASyncDoesNotOverwriteConcurrentPhoneEdits()
    {
        var clients = new ControlledFanvilFactory
        {
            Upload = async (_, ct) =>
            {
                await using var db = factory.CreateDbContext();
                await db.Phones.Where(p => p.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "Changed during delivery"), ct);
                return new FanvilResponse { StatusCode = 200 };
            }
        };
        Assert.True((await CreateService(clients).SyncOneAsync(1)).Success);
        await using var db = factory.CreateDbContext();
        var phone = await db.Phones.SingleAsync(p => p.Id == 1);
        Assert.Equal("Changed during delivery", phone.Name);
        Assert.Equal("Success", phone.LastSyncStatus);
    }

    [Fact]
    public async Task FleetProgressReportsActualCompletedResultsInOrder()
    {
        var updates = new List<SyncProgress>();
        var clients = new ControlledFanvilFactory
        {
            Upload = (host, _) => Task.FromResult(new FanvilResponse { StatusCode = host.EndsWith(".3") ? 500 : 200 })
        };
        var results = await CreateService(clients, concurrency: 3).SyncAllAsync(progress: new InlineProgress<SyncProgress>(updates.Add));
        Assert.Equal(6, results.Count);
        Assert.Equal(Enumerable.Range(0, 7), updates.Select(u => u.Completed));
        Assert.All(updates, u => Assert.Equal(u.Completed, u.Succeeded + u.Failed));
        Assert.Equal(5, updates[^1].Succeeded);
        Assert.Equal(1, updates[^1].Failed);
        Assert.Equal(6, updates.Where(u => u.Latest is not null).Select(u => u.Latest!.PhoneId).Distinct().Count());
    }

    [Fact]
    public async Task DisposingDiscoveryEnumerationCancelsAndJoinsOutstandingWorkers()
    {
        var active = 0;
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new ControlledFanvilFactory
        {
            Get = async (host, ct) =>
            {
                Interlocked.Increment(ref active);
                try
                {
                    if (host.EndsWith(".1")) await secondStarted.Task.WaitAsync(ct);
                    else { secondStarted.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    return new FanvilResponse { StatusCode = 200, Body = "Fanvil" };
                }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        var service = new PhoneDiscoveryService(new Access(), NullLogger<PhoneDiscoveryService>.Instance, clients, new TestApplicationLifetime());
        await using (var iterator = service.ScanAsync("192.0.2.1-5", [], maxConcurrency: 2).GetAsyncEnumerator())
            Assert.True(await iterator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(clients.Created, clients.Disposed);
    }

    [Fact]
    public async Task OversizedDiscoveryRangesAreRejectedBeforeCreatingClients()
    {
        var clients = new ControlledFanvilFactory();
        var service = new PhoneDiscoveryService(new Access(), NullLogger<PhoneDiscoveryService>.Instance, clients, new TestApplicationLifetime());
        await using var iterator = service.ScanAsync("10.0.0.0/8", []).GetAsyncEnumerator();
        await Assert.ThrowsAsync<ArgumentException>(() => iterator.MoveNextAsync().AsTask());
        Assert.Equal(0, clients.Created);
    }

    [Fact]
    public async Task CancelledConfirmationReleasesTheDialogForTheNextAction()
    {
        using var feedback = new UiFeedback();
        using var cancellation = new CancellationTokenSource();
        var pending = feedback.ConfirmAsync("Delete", "Confirm", ct: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(feedback.ActivePrompt);
        var next = feedback.ConfirmAsync("Next", "Confirm");
        feedback.Resolve(DialogChoice.Primary);
        Assert.True(await next);
    }

    [Fact]
    public async Task ApplicationShutdownCancelsActiveHandsetWorkAndFinalizesHistory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new ControlledFanvilFactory
        {
            Login = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return (true, "OK"); }
        };
        using var stopping = new CancellationTokenSource();
        var service = new PhoneSyncService(factory, NullLogger<PhoneSyncService>.Instance, new Access(), clients,
            Options.Create(new PhoneOperationOptions()), new TestApplicationLifetime(stopping.Token));
        var work = service.SyncOneAsync(1);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
        await using var db = factory.CreateDbContext();
        Assert.Equal("Cancelled", (await db.SyncRuns.SingleAsync()).Status);
    }

    [Fact]
    public async Task IncompletePostResponseIsNotReplayedOrReportedAsSuccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new FanvilHttpClient("127.0.0.1", false, ((IPEndPoint)listener.LocalEndpoint).Port);
        var request = client.PostFormAsync("/lines.htm", new Dictionary<string, string> { ["Apply"] = "1" });
        using (var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5)))
        {
            var stream = accepted.GetStream();
            var buffer = new byte[4096];
            await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nshort"));
            accepted.Client.Shutdown(SocketShutdown.Send);
            await Assert.ThrowsAsync<IOException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SocketReadAndTlsHandshakeHonorCancellation(bool useTls)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new FanvilHttpClient("127.0.0.1", useTls, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync("/", cancellation.Token);
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(listener.Pending());
    }

    private PhoneSyncService CreateService(IFanvilClientFactory clients, int timeoutSeconds = 60, int concurrency = 2) =>
        new(factory, NullLogger<PhoneSyncService>.Instance, new Access(), clients,
            Options.Create(new PhoneOperationOptions { MaxConcurrentSyncs = concurrency, TimeoutSeconds = timeoutSeconds }), new TestApplicationLifetime());
    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private sealed class Access(bool administrator = true) : IUserAccess
    {
        public Task RequireAuthenticatedAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task RequireAdministratorAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return administrator ? Task.CompletedTask : Task.FromException(new UnauthorizedAccessException()); }
    }
}
