using FancilPhones.Application;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Tests;

public sealed partial class AsyncOperationTests
{
    [Fact]
    public async Task SipReadIsAuditedWithoutChangingDeliveryMetadataAndPushUpdatesOnlySipReferences()
    {
        var device = new SipDevice(); var service = CreateService(device);
        var snapshot = await service.ReadSipSettingsAsync(1, 2);
        Assert.Equal(2, snapshot.LineIndex); Assert.Equal("Registered", snapshot.LineStatus);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("SipRead", (await db.SyncRuns.SingleAsync()).Action);
            Assert.Null((await db.Phones.FindAsync(1))!.LastSyncedAt);
            var phone = (await db.Phones.FindAsync(1))!;
            phone.Name = "Concurrent fleet edit";
            await db.SaveChangesAsync();
        }
        var desired = snapshot.Settings.Copy(); desired.DisplayName = "Edited handset"; desired.ReplacementPassword = "private-new-secret";
        var result = await service.PushSipSettingsAsync(snapshot, desired);
        Assert.True(result.ok, result.message);
        await using (var db = factory.CreateDbContext())
        {
            var phone = (await db.Phones.FindAsync(1))!;
            Assert.Equal("Concurrent fleet edit", phone.Name);
            Assert.Equal("Edited handset", phone.SipDisplayName); Assert.Equal(2, phone.SipLineIndex);
            Assert.Equal("Success", phone.LastSyncStatus);
            var history = await db.SyncRuns.ToListAsync();
            Assert.DoesNotContain(history, r => r.Message?.Contains("private-new-secret") == true);
            Assert.Equal(2, history.Count); Assert.Equal("Push", history.Last().Action);
        }
    }

    [Fact]
    public async Task LegacyPushPreservesTheSeparateAuthenticationIdentity()
    {
        var device = new SipDevice();
        await using (var db = factory.CreateDbContext())
        {
            var phone = (await db.Phones.FindAsync(1))!;
            phone.SipExtension = "999"; phone.SipLineIndex = 2;
            await db.SaveChangesAsync();
        }
        Assert.True((await CreateService(device).PushSipDisplayNameAsync(1)).ok);
        Assert.Equal("999", device.Lines[2]["SIP_PhoneNum_R"]);
        Assert.Equal("auth-different-from-extension", device.Lines[2]["SIP_RegUser_R"]);
        Assert.Equal("236", device.Lines[1]["SIP_PhoneNum_R"]);
    }

    [Fact]
    public async Task SimultaneousSipPushesCannotOverwriteTheSameLoadedBaseline()
    {
        var device = new SipDevice(); var service = CreateService(device);
        var snapshot = await service.ReadSipSettingsAsync(1, 1);
        var first = snapshot.Settings.Copy(); first.DisplayName = "First edit";
        var second = snapshot.Settings.Copy(); second.DisplayName = "Second edit";
        var results = await Task.WhenAll(service.PushSipSettingsAsync(snapshot, first), service.PushSipSettingsAsync(snapshot, second));
        Assert.Single(results, r => r.ok); Assert.Single(results, r => !r.ok);
        Assert.Single(device.Applies);
    }

    [Fact]
    public async Task CancellationAfterApplyRecordsUncertaintyWithoutReplayingTheWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var device = new SipDevice { OnApply = cancellation.Cancel }; var service = CreateService(device);
        var snapshot = await service.ReadSipSettingsAsync(1, 1);
        var desired = snapshot.Settings.Copy(); desired.DisplayName = "Applied before cancellation";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PushSipSettingsAsync(snapshot, desired, cancellation.Token));
        Assert.Single(device.Applies); Assert.Equal(desired.DisplayName, device.Lines[1]["SIP_DisPlayName_R"]);
        await using var db = factory.CreateDbContext();
        var run = await db.SyncRuns.SingleAsync(r => r.Action == "Push");
        Assert.Equal("Cancelled", run.Status); Assert.Contains("may have applied", run.Message);
    }

    [Fact]
    public async Task LongHandsetUsernameIsPreservedWithoutOverflowingTheLegacyFleetReference()
    {
        var device = new SipDevice(); var service = CreateService(device);
        var snapshot = await service.ReadSipSettingsAsync(1, 1);
        var desired = snapshot.Settings.Copy(); desired.Username = new string('x', 39);
        Assert.True((await service.PushSipSettingsAsync(snapshot, desired)).ok);
        Assert.Equal(desired.Username, device.Lines[1]["SIP_PhoneNum_R"]);
        await using var db = factory.CreateDbContext();
        Assert.Null((await db.Phones.FindAsync(1))!.SipExtension);
    }

    [Fact]
    public async Task CancelledSipReadPersistsCancellationAndReleasesHandsetSession()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clients = new ControlledFanvilFactory
        {
            Login = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return (true, "OK"); }
        };
        using var cancellation = new CancellationTokenSource();
        var task = CreateService(clients).ReadSipSettingsAsync(1, 1, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await using var db = factory.CreateDbContext();
        var run = await db.SyncRuns.SingleAsync(); Assert.Equal("SipRead", run.Action); Assert.Equal("Cancelled", run.Status);
        Assert.Equal(1, clients.LoggedOut); Assert.Equal(1, clients.Disposed);
    }
}

