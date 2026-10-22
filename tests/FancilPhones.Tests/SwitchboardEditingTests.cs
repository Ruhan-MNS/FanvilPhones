using FancilPhones.Application;
using FancilPhones.Domain;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Tests;

public sealed class SwitchboardEditingTests
{
    [Fact]
    public void AlphabeticalInsertionMovesWholeRowsAcrossAllPagesWithoutMovingLineKeys()
    {
        var layout = SwitchboardFixture.Layout();
        foreach (var key in layout.Keys.Where(k => !SwitchboardRules.LegacyDirectoryReserved(k)))
        {
            key.Fields["Type"] = "1"; key.Fields["Title"] = $"Contact {61 - (key.Page - 1) * 12 - key.Slot:00}";
            key.Fields["Value"] = $"{200 + key.Page * 12 + key.Slot}";
        }
        layout.Keys[10].Fields["Type"] = "2";
        var last = layout.Keys[^1]; last.Fields["Title"] = "Aaron"; last.Fields["Value"] = "777|0120000000";
        last.Fields["IconType"] = "Red"; last.Fields["PICKUP"] = "*8777";
        var before = layout.Fingerprint();
        var sorted = SwitchboardEditing.Sort(layout);
        Assert.Equal(before, layout.Fingerprint());
        Assert.Equal("Aaron", sorted.Keys[2].Name);
        Assert.Equal(last.Fields, sorted.Keys[2].Fields);
        Assert.Equal(layout.Keys[10].Fields, sorted.Keys[10].Fields);
        Assert.Equal(layout.Keys[0].Fields, sorted.Keys[0].Fields);
        Assert.Equal(layout.Keys[1].Fields, sorted.Keys[1].Fields);
        Assert.Equal(60, sorted.Keys.Count);
        Assert.NotEqual(layout.Keys.Single(k => k.Page == 5 && k.Slot == 12).Name, sorted.Keys.Single(k => k.Page == 5 && k.Slot == 12).Name);
        SwitchboardEditing.ValidateDraft("192.168.55.133", layout, sorted);
    }

    [Theory]
    [InlineData("subtype")]
    [InlineData("settings")]
    [InlineData("line")]
    [InlineData("length")]
    [InlineData("duplicate")]
    [InlineData("control")]
    [InlineData("field")]
    public void UnsafeDraftsAreRejected(string reason)
    {
        var baseline = SwitchboardFixture.Layout(); var draft = SwitchboardEditing.Clone(baseline);
        switch (reason)
        {
            case "subtype": SwitchboardKeyOptions.ChangeType(draft.Keys[0], "3", "1"); draft.Keys[0].Fields["KeyeventSubType"] = "999"; break;
            case "settings": draft.Settings["DSS_Key_Home_Page_RW"] = "4"; break;
            case "line": draft.Keys[2].Fields["Line"] = "99"; break;
            case "length": draft.Keys[2].Fields["Title"] = new string('é', 32); break;
            case "duplicate": draft.Keys[2].Fields["Title"] = "Alpha"; break;
            case "control": draft.Keys[2].Fields["Value"] = "200\n"; break;
            case "field": draft.Keys[2].Fields["Injected"] = "value"; break;
        }
        Assert.Throws<SwitchboardValidationException>(() => SwitchboardEditing.ValidateDraft("192.168.55.133", baseline, draft));
    }
}

public sealed partial class SwitchboardServiceTests
{
    [Theory]
    [InlineData("10.26.7.44")]
    [InlineData("198.51.100.42")]
    [InlineData("203.0.113.88")]
    public async Task FleetAddressesAndCapturedKeyValuesDriveTheEntireEditingWorkflow(string ip)
    {
        await using (var db = factory.CreateDbContext())
        {
            var phone = await db.Phones.SingleAsync(p => p.Id == 1);
            phone.IpAddress = ip; phone.Name = "Operations desk"; await db.SaveChangesAsync();
        }
        SwitchboardKeyOptions.ChangeType(device.Layout.Keys[0], "3", "1");
        SwitchboardKeyOptions.ChangeSubtype(device.Layout.Keys[0], "1");
        device.Layout.Keys[0].Fields["Title"] = "Desk DND";
        device.Layout.Keys[1].Fields["Title"] = "After-hours support";
        device.Layout.Keys[1].Fields["Value"] = "812|0123456789";
        device.Layout.Keys[1].Fields["MemorySubType"] = "92";
        var original = device.Layout.Fingerprint();
        await Service().CaptureAsync(1);
        var phoneState = (await Service().GetStateAsync()).Phones.Single();
        Assert.Equal(ip, phoneState.IpAddress); Assert.Equal("Operations desk", phoneState.Name);
        Assert.Null(phoneState.ConfigurationWarning);
        var editor = await Service().GetEditorAsync(1);
        Assert.Equal(original, editor.Layout.Fingerprint());
        editor.Layout.Keys[1].Fields["Title"] = "Night support";
        editor.Layout.Keys[1].Fields["Value"] = "813|0123456789";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        var preview = await Service().PreviewDraftAsync(1, 1, false);
        Assert.Equal(ip, preview.IpAddress); Assert.Equal(0, device.Writes);
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Equal("Desk DND", device.Layout.Keys[0].Name);
        Assert.Equal("3", device.Layout.Keys[0].Get("Type"));
        Assert.Equal("Night support", device.Layout.Keys[1].Name);
        Assert.Equal("813|0123456789", device.Layout.Keys[1].Get("Value"));
        Assert.Equal("92", device.Layout.Keys[1].Get("MemorySubType"));
        Assert.Equal("Success", (await Service().DeployAsync((await Service().PreviewRestoreAsync(preview.Id)).Id)).Status);
        Assert.Equal(original, device.Layout.Fingerprint());
    }

    [Fact]
    public async Task DraftsPersistWithoutHandsetWritesAndRejectStaleEditors()
    {
        await Service().CaptureAsync(1); var before = device.Layout.Fingerprint();
        var editor = await Service().GetEditorAsync(1);
        editor.Layout.Keys[2].Fields["Value"] = "299|0120000000";
        var saved = await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, sortAlphabetically: false);
        Assert.False((await Service().GetEditorAsync(1)).SortAlphabetically);
        Assert.NotEqual(editor.Revision, saved.Revision);
        Assert.Equal(before, device.Layout.Fingerprint()); Assert.Equal(0, device.Writes);
        Assert.Equal("299", (await Service().GetEditorAsync(1)).Layout.Keys[2].Extension);
        Assert.True((await Service().GetStateAsync()).Phones.Single().HasDraft);
        await Assert.ThrowsAsync<SwitchboardValidationException>(() => Service().SaveDraftAsync(1, editor.Revision, editor.Layout));
        editor.Layout.Keys[0].Fields["Line"] = "99";
        await Assert.ThrowsAsync<SwitchboardValidationException>(() => Service().SaveDraftAsync(1, saved.Revision, editor.Layout));
        await Service().CaptureAsync(1);
        Assert.Equal(before, (await Service().GetEditorAsync(1)).Layout.Fingerprint());
    }

    [Fact]
    public async Task CompatiblePhonesCanBeAddedFromFleetAtNewAddressesAndRemovedWithoutTouchingFleetOrKeys()
    {
        await using (var db = factory.CreateDbContext()) { db.Phones.Add(new() { Id = 3, Name = "New switchboard", IpAddress = "192.168.55.199" }); await db.SaveChangesAsync(); }
        await Service().CaptureAsync(3);
        Assert.Equal(3, (await Service().GetStateAsync()).Phones.Single().Id);
        var editor = await Service().GetEditorAsync(3);
        var preview = await Service().PreviewDraftAsync(3, 3, true);
        await Service().RemovePhoneAsync(3);
        Assert.Empty((await Service().GetStateAsync()).Phones);
        Assert.Contains((await Service().GetStateAsync()).FleetPhones, p => p.Id == 3);
        Assert.Equal(0, device.Writes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        await using var check = factory.CreateDbContext(); Assert.Single(await check.SwitchboardDeployments.ToListAsync());
    }

    private async Task<SwitchboardLayout> AddReceptionAsync()
    {
        var layout = SwitchboardFixture.Layout("192.168.55.101");
        foreach (var key in layout.Keys) key.Fields["Line"] = "2";
        layout.Keys[3].Fields["Value"] = "905|0821111111";
        device.Layouts["192.168.55.101"] = layout;
        await using (var db = factory.CreateDbContext()) { db.Phones.Add(new() { Id = 3, Name = "Reception", IpAddress = "192.168.55.101" }); await db.SaveChangesAsync(); }
        await Service().CaptureAsync(3); return layout;
    }

    [Fact]
    public async Task SelectedPhoneSyncCopiesEveryPageAndCopiesF1F2WhilePreservingTargetLineAndOptionalNumbers()
    {
        await Service().CaptureAsync(1); var reception = await AddReceptionAsync();
        var editor = await Service().GetEditorAsync(1);
        editor.Layout.Keys[3].Fields["Value"] = "700|0122222222";
        var page5 = editor.Layout.Keys[^1]; page5.Fields["Type"] = "1"; page5.Fields["Title"] = "Workshop"; page5.Fields["Value"] = "225";
        page5.Fields["MemorySubType"] = "92"; page5.Fields["PICKUP"] = "*8225"; page5.Fields["IconType"] = "Red";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout);
        var own = await Service().PreviewDraftAsync(1, 1, true);
        var other = await Service().PreviewDraftAsync(1, 3, true);
        var target = SwitchboardLayout.FromJson(other.TargetJson);
        Assert.Equal(reception.Keys[0].Fields, target.Keys[0].Fields); Assert.Equal("Reception", target.Keys[1].Name); Assert.Equal("201", target.Keys[1].Extension);
        Assert.Equal("905|0821111111", target.Keys[3].Get("Value"));
        Assert.All(target.Keys, k => Assert.Equal("2", k.Get("Line")));
        Assert.Equal("Workshop", target.Keys[^1].Name); Assert.Equal("*8225", target.Keys[^1].Get("PICKUP")); Assert.Equal("Red", target.Keys[^1].Get("IconType"));
        var exact = await Service().PreviewDraftAsync(1, 3, false);
        Assert.Equal("700|0122222222", SwitchboardLayout.FromJson(exact.TargetJson).Keys[3].Get("Value"));
        Assert.Equal(0, device.Writes);
        // Sending to the source first must not invalidate the remaining reviewed targets.
        Assert.Equal("Success", (await Service().DeployAsync(own.Id)).Status);
        Assert.Equal("Success", (await Service().DeployAsync(other.Id)).Status);
        Assert.Equal(target.Fingerprint(), reception.Fingerprint());
        Assert.Equal("Workshop", (await Service().GetEditorAsync(3)).Layout.Keys[^1].Name);
        Assert.Equal("700|0122222222", device.Layout.Keys[3].Get("Value"));
    }

    [Fact]
    public async Task SourceDraftChangesAndTargetDriftInvalidateCustomPreviewsBeforeWrites()
    {
        await Service().CaptureAsync(1); await AddReceptionAsync();
        var preview = await Service().PreviewDraftAsync(1, 3, true);
        var editor = await Service().GetEditorAsync(1); editor.Layout.Keys[2].Fields["Value"] = "288";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        Assert.Equal(0, device.Writes);
        var next = await Service().PreviewDraftAsync(1, 3, true);
        device.Layouts["192.168.55.101"].Keys[2].Fields["Value"] = "289";
        Assert.Equal("Failed", (await Service().DeployAsync(next.Id)).Status); Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task DisabledPhonesAndUnregisteredTargetsCannotReceiveDraftSync()
    {
        await Service().CaptureAsync(1);
        var logins = device.Logins;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().PreviewDraftAsync(1, 2, true));
        Assert.Equal(logins, device.Logins);
        await using (var db = factory.CreateDbContext()) { (await db.Phones.FindAsync(1))!.Enabled = false; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().GetEditorAsync(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().PreviewDraftAsync(1, 1, true));
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task PartialCustomSyncCanBeRestoredFromItsBackupAndActiveLeasesBlockEditsAndRemoval()
    {
        await Service().CaptureAsync(1); var before = device.Layout.Fingerprint(); var editor = await Service().GetEditorAsync(1);
        editor.Layout.Keys[2].Fields["Value"] = "299";
        editor.Layout.Keys[12].Fields["Type"] = "1"; editor.Layout.Keys[12].Fields["Title"] = "Another"; editor.Layout.Keys[12].Fields["Value"] = "298";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout);
        var preview = await Service().PreviewDraftAsync(1, 1, true);
        await using (var db = factory.CreateDbContext()) { (await db.SwitchboardProfiles.SingleAsync()).LeaseUntil = DateTime.UtcNow.AddMinutes(2); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().SaveDraftAsync(1, editor.Revision, editor.Layout));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().RemovePhoneAsync(1));
        await using (var db = factory.CreateDbContext()) { (await db.SwitchboardProfiles.SingleAsync()).LeaseUntil = null; await db.SaveChangesAsync(); }
        device.FailPage = 2;
        var failed = await Service().DeployAsync(preview.Id);
        Assert.Equal("Failed", failed.Status); Assert.Equal(1, device.Writes); Assert.NotEqual(before, device.Layout.Fingerprint());
        device.FailPage = 0;
        var restore = await Service().PreviewRestoreAsync(failed.Id);
        Assert.Equal("Success", (await Service().DeployAsync(restore.Id)).Status);
        Assert.Equal(before, device.Layout.Fingerprint());
        Assert.Equal(before, (await Service().GetEditorAsync(1)).Layout.Fingerprint());
    }

    [Fact]
    public async Task AnExplicitEmptyDraftCanClearAllSixtyPositionsIncludingF1AndF2()
    {
        await Service().CaptureAsync(1); var editor = await Service().GetEditorAsync(1);
        foreach (var key in editor.Layout.Keys) { key.Fields["Type"] = "0"; key.Fields["Title"] = ""; key.Fields["Value"] = ""; }
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout);
        var preview = await Service().PreviewDraftAsync(1, 1, true);
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.All(device.Layout.Keys, k => Assert.Equal("0", k.Get("Type")));
        Assert.All(device.Layout.Keys, k => Assert.Equal("", k.Name));
    }
}
