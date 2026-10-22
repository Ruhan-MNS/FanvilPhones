using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FancilPhones.Tests;

public sealed partial class AsyncOperationTests
{
    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("TimedOut")]
    [InlineData("Cancelled")]
    public async Task RetryUsesLatestPhonebookStatusAndDoesNotRepeatSuccessfulDisabledRunningOrUnattemptedPhones(string status)
    {
        await using (var db = factory.CreateDbContext())
        {
            // More than one history page must not hide an old unresolved failure.
            db.SyncRuns.Add(new() { PhoneId = 1, Status = status });
            await db.SaveChangesAsync();
            db.SyncRuns.AddRange(Enumerable.Range(0, 60).Select(_ => new SyncRun { PhoneId = 2, Status = "Failed" }));
            await db.SaveChangesAsync();
            db.SyncRuns.AddRange(new SyncRun { PhoneId = 1, Action = "Push", Status = "Success" },
                new SyncRun { PhoneId = 1, Action = "Probe", Status = "Success" },
                new SyncRun { PhoneId = 2, Status = "Success" }, new SyncRun { PhoneId = 3, Status = "TimedOut" },
                new SyncRun { PhoneId = 4, Status = "Failed" }, new SyncRun { PhoneId = 5, Status = "Running" });
            (await db.Phones.SingleAsync(p => p.Id == 4)).Enabled = false;
            await db.SaveChangesAsync();
        }
        var clients = new ControlledFanvilFactory(); var service = CreateService(clients);
        var states = await service.GetStatusAsync();
        Assert.Equal(status, states.Single(p => p.PhoneId == 1).Status);
        Assert.False(states.Single(p => p.PhoneId == 4).CanRetry);
        Assert.Equal("Pending", states.Single(p => p.PhoneId == 6).Status);
        var updates = new List<SyncProgress>();
        var results = await service.SyncFailedAsync(progress: new InlineProgress<SyncProgress>(updates.Add));
        Assert.Equal(new[] { 1, 3 }, results.Select(r => r.PhoneId));
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(2, clients.Created); Assert.Equal(clients.Created, clients.Disposed);
        Assert.Equal(new[] { 0, 1, 2 }, updates.Select(p => p.Completed));
        Assert.Equal(2, updates[^1].Succeeded);
        Assert.Empty(await service.SyncFailedAsync());
        Assert.Equal(2, clients.Created);
        Assert.DoesNotContain(await service.GetStatusAsync(), p => p.CanRetry);
    }

    [Fact]
    public async Task IndividualSelectionDeduplicatesIdsAndCreatesNewAuditedAttemptsOnlyForThosePhones()
    {
        var clients = new ControlledFanvilFactory(); var service = CreateService(clients);
        var results = await service.SyncSelectedAsync([3, 3]);
        Assert.Equal(3, Assert.Single(results).PhoneId);
        Assert.Equal(1, clients.Created);
        await using var db = factory.CreateDbContext();
        Assert.Equal(3, (await db.SyncRuns.SingleAsync()).PhoneId);
        Assert.Equal("Success", (await service.GetStatusAsync()).Single(p => p.PhoneId == 3).Status);
        Assert.All((await service.GetStatusAsync()).Where(p => p.PhoneId != 3), p => Assert.Null(p.LastAttemptAt));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("disabled")]
    public async Task InvalidSelectionsAreRejectedBeforeAnyHandsetIsChanged(string reason)
    {
        await using (var db = factory.CreateDbContext())
        { (await db.Phones.SingleAsync(p => p.Id == 3)).Enabled = false; await db.SaveChangesAsync(); }
        int[] ids = reason switch { "empty" => [], "missing" => [1, 999], _ => [1, 3] };
        var clients = new ControlledFanvilFactory();
        await Assert.ThrowsAsync<PhoneSyncValidationException>(() => CreateService(clients).SyncSelectedAsync(ids));
        Assert.Equal(0, clients.Created);
        await using var check = factory.CreateDbContext(); Assert.Empty(await check.SyncRuns.ToListAsync());
    }

    [Fact]
    public async Task RetryAndIndividualDeliveryRequireAdministratorButStatusRemainsViewable()
    {
        var clients = new ControlledFanvilFactory();
        var service = new PhoneSyncService(factory, NullLogger<PhoneSyncService>.Instance, new Access(false), clients,
            Options.Create(new PhoneOperationOptions()), new TestApplicationLifetime());
        Assert.Equal(6, (await service.GetStatusAsync()).Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SyncFailedAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SyncSelectedAsync([1]));
        Assert.Equal(0, clients.Created);
    }
}
