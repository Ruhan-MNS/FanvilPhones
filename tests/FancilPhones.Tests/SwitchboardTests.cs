using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FancilPhones.Application;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FancilPhones.Tests;

public sealed class SwitchboardLayoutTests
{
    [Theory]
    [InlineData("1", "2")]
    [InlineData("4", "12")]
    [InlineData("5", "6")]
    public void UnsupportedGeometryIsAnExpectedCompatibilityResult(string pages, string slots)
    {
        var html = SwitchboardFixture.Html(SwitchboardFixture.Layout(), 1)
            .Replace("pageNum = '5'", $"pageNum = '{pages}'").Replace("dsskeyNums = '12'", $"dsskeyNums = '{slots}'");
        var error = Assert.Throws<SwitchboardCompatibilityException>(() => FanvilSwitchboardAdapter.ParsePage(html, 1));
        Assert.Contains("five pages of twelve", error.Message);
        Assert.Contains("Phone fleet", error.Message);
    }

    [Theory]
    [InlineData("192.168.55.101")]
    [InlineData("192.168.55.133")]
    public void SortingSpansPagesAndPreservesProtectedKeysAndWholeContactRows(string ip)
    {
        var baseline = SwitchboardFixture.Layout(ip);
        var members = Enumerable.Range(1, 58).Reverse().Select(i => new SwitchboardMember
        { ContactId = i, Contact = new() { Id = i, DisplayName = $"Contact {i:00}", OfficeNumber = $"{300 + i}" } }).ToList();
        var old = baseline.Keys[2];
        old.Fields["Title"] = "Contact 01"; old.Fields["Value"] = "301|012 000 0000";
        old.Fields["IconType"] = "Red"; old.Fields["PICKUP"] = "*8301";
        var result = SwitchboardRules.Generate(ip, baseline, members, []);
        Assert.Equal("Contact 01", result.Keys[2].Name);
        Assert.Equal("Contact 10", result.Keys.Single(k => k.Page == 1 && k.Slot == 12).Name);
        Assert.Equal("Contact 11", result.Keys.Single(k => k.Page == 2 && k.Slot == 1).Name);
        Assert.Equal("Contact 58", result.Keys[^1].Name);
        Assert.Equal("012 000 0000", result.Keys[2].Value2);
        Assert.Equal("Red", result.Keys[2].Get("IconType"));
        Assert.Equal("*8301", result.Keys[2].Get("PICKUP"));
        Assert.Equal(baseline.Keys[0].Fields, result.Keys[0].Fields);
        Assert.Equal(baseline.Keys[1].Fields, result.Keys[1].Fields);
        Assert.NotSame(baseline.Keys[0].Fields, result.Keys[0].Fields);
        Assert.Equal(baseline.Settings, result.Settings);
        Assert.All(result.Keys.Where(k => !SwitchboardRules.LegacyDirectoryReserved(k)), k => Assert.Equal("1", k.Get("Line")));
    }

    [Fact]
    public void OverridesAndCapturedIdentitySurviveContactRename()
    {
        var baseline = SwitchboardFixture.Layout();
        var saved = baseline.Keys[2] with { Fields = new(baseline.Keys[2].Fields) };
        saved.Fields["Value"] = "205|0120000000"; saved.Fields["MemorySubType"] = "92";
        var member = new SwitchboardMember { ContactId = 1, Contact = new() { DisplayName = "Changed name", OfficeNumber = "204" } };
        var result = SwitchboardRules.Generate("192.168.55.133", baseline, [member],
            [new() { ContactId = 1, Extension = "205", CapturedKeyJson = JsonSerializer.Serialize(saved) }]);
        Assert.Equal("Changed name", result.Keys[2].Name);
        Assert.Equal("205", result.Keys[2].Extension);
        Assert.Equal("0120000000", result.Keys[2].Value2);
        Assert.Equal("92", result.Keys[2].Get("MemorySubType"));
    }

    [Fact]
    public void CapacityDuplicateNamesSpecialKeysAndStandardPhonesAreRejected()
    {
        var layout = SwitchboardFixture.Layout();
        var member = new SwitchboardMember { ContactId = 1, Contact = new() { DisplayName = "Test", OfficeNumber = "200" } };
        Assert.Single(SwitchboardRules.Generate("192.168.55.199", layout, [member], []).Keys, k => k.Name == "Test");
        Assert.Throws<InvalidOperationException>(() => SwitchboardRules.ValidateGeometry("192.168.55.136", new(layout.Keys.Take(2).ToList(), [])));
        Assert.Throws<SwitchboardValidationException>(() => SwitchboardRules.Generate("192.168.55.133", layout, [], []));
        Assert.Throws<InvalidOperationException>(() => SwitchboardRules.Generate("192.168.55.133", layout, Enumerable.Repeat(member, 59).ToList(), []));
        Assert.Throws<InvalidOperationException>(() => SwitchboardRules.Generate("192.168.55.133", layout, [member, member], []));
        layout.Keys[12].Fields["Type"] = "3";
        Assert.Throws<InvalidOperationException>(() => SwitchboardRules.Generate("192.168.55.133", layout, [member], []));
    }

    [Fact]
    public void ExplicitSortNamesAndAccentsDoNotAlterDisplayedNames()
    {
        var members = new List<SwitchboardMember>
        {
            new() { ContactId = 1, SortName = "A", Contact = new() { DisplayName = "Zoë", OfficeNumber = "201" } },
            new() { ContactId = 2, Contact = new() { DisplayName = "Anné", OfficeNumber = "202" } }
        };
        var result = SwitchboardRules.Generate("192.168.55.133", SwitchboardFixture.Layout(), members, []);
        Assert.Equal("Zoë", result.Keys[2].Name); Assert.Equal("Anné", result.Keys[3].Name);
    }

    [Fact]
    public void AdapterParsesEscapedValuesAndPostsTheCombinedSecondaryNumber()
    {
        var layout = SwitchboardFixture.Layout();
        layout.Keys[2].Fields["Title"] = "Anné & \"Test\"";
        layout.Keys[2].Fields["Value"] = "204|012 000 0000";
        var parsed = FanvilSwitchboardAdapter.ParsePage(SwitchboardFixture.Html(layout, 1), 1);
        Assert.Equal(layout.Keys[2].Fields, parsed.Keys[2].Fields);
        var fields = FanvilSwitchboardAdapter.BuildFields(parsed.Keys);
        Assert.Equal("204|012 000 0000", fields["SIDE_Key_Value_RW(3)"]);
        Assert.Equal("204", fields["SIDE_Key_Value1_RW(3)"]);
        Assert.Equal("012 000 0000", fields["SIDE_Key_Value2_RW(3)"]);
        Assert.DoesNotContain("DSS_Key_Home_Page_RW", fields.Keys);
        Assert.Throws<InvalidDataException>(() => FanvilSwitchboardAdapter.ParsePage(SwitchboardFixture.Html(layout, 1), 2));
        Assert.Throws<InvalidDataException>(() => FanvilSwitchboardAdapter.ParsePage("<form><input id='logonButton'></form>", 1));
        Assert.Throws<InvalidDataException>(() => FanvilSwitchboardAdapter.ParsePage(SwitchboardFixture.Html(layout, 1).Replace("SIDE_Key_Value_RW(3)", "MissingField"), 1));
    }

    [Fact]
    public async Task ChunkedResponsesAreDecodedAcrossPacketAndUtf8Boundaries()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new FanvilHttpClient("127.0.0.1", false, ((IPEndPoint)listener.LocalEndpoint).Port);
        var request = client.GetAsync("/sideKey.htm");
        using var accepted = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var stream = accepted.GetStream(); var buffer = new byte[4096]; var received = await stream.ReadAsync(buffer);
        Assert.True(received > 0);
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"u8.ToArray());
        var body = Encoding.UTF8.GetBytes("Anné & Reception");
        for (var i = 0; i < body.Length; i++)
        {
            await stream.WriteAsync("1;ext=test\r\n"u8.ToArray()); await stream.WriteAsync(body.AsMemory(i, 1)); await stream.WriteAsync("\r\n"u8.ToArray());
        }
        await stream.WriteAsync("0\r\nX-Test: done\r\n\r\n"u8.ToArray());
        Assert.Equal("Anné & Reception", (await request.WaitAsync(TimeSpan.FromSeconds(5))).Body);
    }
}

public sealed partial class SwitchboardServiceTests : IAsyncLifetime
{
    [Fact]
    public async Task AnIncompatibleRecaptureKeepsTheExistingBaselineAndDraft()
    {
        await Service().CaptureAsync(1);
        var editor = await Service().GetEditorAsync(1); editor.Layout.Keys[2].Fields["Value"] = "299";
        var saved = await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        device.UnsupportedLayout = true;
        var error = await Assert.ThrowsAsync<SwitchboardCompatibilityException>(() => Service().CaptureAsync(1));
        Assert.Contains("saved layout was kept", error.Message);
        var after = await Service().GetEditorAsync(1);
        Assert.Equal(saved.Revision, after.Revision);
        Assert.Equal(saved.Layout.Fingerprint(), after.Layout.Fingerprint());
        Assert.Equal(saved.Baseline.Fingerprint(), after.Baseline.Fingerprint());
        Assert.Equal(0, device.Writes);
    }

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private Factory factory = null!;
    private readonly SwitchboardFixture.Device device = new();
    private SwitchboardService Service(bool admin = true) => new(factory, new Access(admin), device,
        new(), Options.Create(new PhoneOperationOptions()), new TestApplicationLifetime(), NullLogger<SwitchboardService>.Instance);
    public async Task InitializeAsync()
    {
        await connection.OpenAsync(); factory = new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await using var db = factory.CreateDbContext(); await db.Database.EnsureCreatedAsync();
        db.Phones.AddRange(new Phone { Id = 1, Name = "Switchboard", IpAddress = "192.168.55.133" }, new Phone { Id = 2, Name = "Standard", IpAddress = "192.168.55.136" });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => connection.DisposeAsync().AsTask();
    private async Task ReadyAsync()
    { await Service().CaptureAsync(1); await Service().ImportContactsAsync(1); }

    [Fact]
    public async Task EmptyDirectoryExplainsHowToProceedWithoutContactingThePhoneOrSavingAPreview()
    {
        await Service().CaptureAsync(1);
        var logins = device.Logins;
        var error = await Assert.ThrowsAsync<SwitchboardValidationException>(() => Service().PreviewAsync(1));
        Assert.Contains("Import contacts", error.Message);
        Assert.Contains("Include", error.Message);
        Assert.Equal(logins, device.Logins);
        Assert.Equal(0, device.Writes);
        await using (var db = factory.CreateDbContext()) Assert.Empty(await db.SwitchboardDeployments.ToListAsync());

        await Service().ImportContactsAsync(1);
        Assert.Equal("Preview", (await Service().PreviewAsync(1)).Status);
        Assert.Equal(0, device.Writes);
    }

    [Fact]
    public async Task NonAdministratorsNeverReachDevicesAndStandardHandsetsCannotBeEnrolled()
    {
        var viewer = Service(false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.CaptureAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.PreviewAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.DeployAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.PreviewRestoreAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.SetMemberAsync(1, true, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.ImportContactsAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.SaveOverrideAsync(1, 1, null, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.GetEditorAsync(1));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.SaveDraftAsync(1, Guid.NewGuid(), device.Layout));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.PreviewDraftAsync(1, 2, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewer.RemovePhoneAsync(1));
        Assert.Equal(0, device.Logins);
        var incompatibility = await Assert.ThrowsAsync<SwitchboardCompatibilityException>(() => Service().CaptureAsync(2));
        Assert.Contains("Standard", incompatibility.Message);
        Assert.Contains("Synchronization", incompatibility.Message);
        Assert.Equal(1, device.Logins);
        Assert.Equal(0, device.Writes);
        Assert.Empty((await Service().GetStateAsync()).Phones);
        await using var check = factory.CreateDbContext();
        Assert.Empty(await check.SwitchboardProfiles.ToListAsync());
        Assert.True((await check.Phones.SingleAsync(p => p.Id == 2)).Enabled);
    }

    [Fact]
    public async Task CaptureAndImportKeepExistingMasterNumbersAndRecordDifferences()
    {
        await using (var db = factory.CreateDbContext())
        { db.Contacts.Add(new() { DisplayName = "Zulu", OfficeNumber = "999" }); await db.SaveChangesAsync(); }
        var before = device.Layout.Fingerprint();
        await ReadyAsync();
        Assert.Equal(before, device.Layout.Fingerprint()); Assert.Equal(0, device.Writes);
        var state = await Service().GetStateAsync();
        Assert.Equal(2, state.Members.Count);
        var contact = state.Contacts.Single(c => c.DisplayName == "Zulu");
        Assert.Equal("999", contact.OfficeNumber);
        Assert.Equal("203", state.Overrides.Single(o => o.ContactId == contact.Id).Extension);
        var preview = await Service().PreviewAsync(1);
        Assert.Equal("203", SwitchboardLayout.FromJson(preview.TargetJson).Keys.Single(k => k.Name == "Zulu").Extension);
    }

    [Fact]
    public async Task SuccessfulDeliveryIsReadBackAndCannotBeReplayed()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        Assert.Equal(0, device.Writes);
        var result = await Service().DeployAsync(preview.Id);
        Assert.Equal("Success", result.Status);
        Assert.Equal("Alpha", device.Layout.Keys[2].Name); Assert.Equal("Zulu", device.Layout.Keys[3].Name);
        Assert.Equal(SwitchboardLayout.FromJson(preview.TargetJson).Fingerprint(), device.Layout.Fingerprint());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        await using var db = factory.CreateDbContext();
        var saved = await db.SwitchboardDeployments.SingleAsync();
        Assert.Equal("Success", saved.Status); Assert.NotNull(saved.FinishedAt);
        Assert.Equal(result.BeforeJson, saved.BeforeJson);
        Assert.Null((await db.SwitchboardProfiles.SingleAsync()).LeaseId);
    }

    [Fact]
    public async Task ChangedMasterOrExpiredPreviewIsRejectedBeforeAnyHandsetWrites()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        await using (var db = factory.CreateDbContext())
        { (await db.Contacts.FirstAsync()).OfficeNumber = "299"; await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        Assert.Equal(0, device.Writes);
        await using (var db = factory.CreateDbContext())
        { (await db.SwitchboardDeployments.SingleAsync()).CreatedAt = DateTime.UtcNow.AddHours(-1); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
    }

    [Fact]
    public async Task HandsetDriftAndConcurrentDeliveryAreRejected()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        await using (var db = factory.CreateDbContext())
        { var profile = await db.SwitchboardProfiles.SingleAsync(); profile.LeaseUntil = DateTime.UtcNow.AddMinutes(2); await db.SaveChangesAsync(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        await using (var db = factory.CreateDbContext())
        { (await db.SwitchboardProfiles.SingleAsync()).LeaseUntil = null; await db.SaveChangesAsync(); }
        device.Layout.Keys[2].Fields["Value"] = "299";
        var result = await Service().DeployAsync(preview.Id);
        Assert.Equal("Failed", result.Status); Assert.Equal(0, device.Writes);
        Assert.Contains("changed since preview", result.Message);
    }

    [Fact]
    public async Task PartialDeliveryRetainsBackupAndRestorationUsesAnotherVerifiedPreview()
    {
        await ReadyAsync();
        await using (var db = factory.CreateDbContext())
        {
            var contacts = Enumerable.Range(1, 12).Select(i => new Contact { DisplayName = $"B{i:00}", OfficeNumber = $"{300 + i}" }).ToList();
            db.Contacts.AddRange(contacts); await db.SaveChangesAsync();
            db.SwitchboardMembers.AddRange(contacts.Select(c => new SwitchboardMember { ContactId = c.Id })); await db.SaveChangesAsync();
        }
        var before = device.Layout.Fingerprint(); var preview = await Service().PreviewAsync(1);
        device.FailPage = 2;
        var failed = await Service().DeployAsync(preview.Id);
        Assert.Equal("Failed", failed.Status); Assert.Equal(1, device.Writes);
        Assert.NotEqual(before, device.Layout.Fingerprint());
        device.FailPage = 0;
        var restore = await Service().PreviewRestoreAsync(failed.Id);
        Assert.Equal("Preview", restore.Status); Assert.Equal(1, device.Writes);
        Assert.Equal("Success", (await Service().DeployAsync(restore.Id)).Status);
        Assert.Equal(before, device.Layout.Fingerprint());
    }

    [Fact]
    public async Task ExplicitOverridesInvalidatePreviewAndCanClearSecondaryNumbers()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        var member = (await Service().GetStateAsync()).Members.First();
        await Service().SaveOverrideAsync(1, member.ContactId, "700", "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        var next = await Service().PreviewAsync(1);
        var row = SwitchboardLayout.FromJson(next.TargetJson).Keys.Single(k => k.Name == member.Contact.DisplayName);
        Assert.Equal("700", row.Extension); Assert.Equal("", row.Value2);
    }

    [Fact]
    public async Task UpdatingAnImportedMasterNumberPropagatesWithoutReimporting()
    {
        await ReadyAsync();
        await using (var db = factory.CreateDbContext())
        { (await db.Contacts.SingleAsync(c => c.DisplayName == "Alpha")).OfficeNumber = "777"; await db.SaveChangesAsync(); }
        var preview = await Service().PreviewAsync(1);
        Assert.Equal("777", SwitchboardLayout.FromJson(preview.TargetJson).Keys.Single(k => k.Name == "Alpha").Extension);
    }

    [Theory]
    [InlineData("192.168.55.101")]
    [InlineData("192.168.55.113")]
    [InlineData("192.168.55.133")]
    [InlineData("192.168.55.147")]
    [InlineData("192.168.55.150")]
    public async Task CustomHeaderKeysDoNotWarnOrRequireAnIpSpecificCorrection(string ip)
    {
        await using (var db = factory.CreateDbContext())
        { (await db.Phones.SingleAsync(p => p.Id == 1)).IpAddress = ip; await db.SaveChangesAsync(); }
        SwitchboardKeyOptions.ChangeType(device.Layout.Keys[0], "3", "1");
        SwitchboardKeyOptions.ChangeSubtype(device.Layout.Keys[0], "1");
        device.Layout.Keys[1].Fields["Title"] = "Support";
        device.Layout.Keys[1].Fields["Value"] = "299";
        device.Layout.Keys[1].Fields["MemorySubType"] = "";
        device.Layout.Keys[1].Fields["Line"] = "-1";
        var before = device.Layout.ToJson();
        await ReadyAsync();
        Assert.Null((await Service().GetStateAsync()).Phones.Single().ConfigurationWarning);
        var preview = await Service().PreviewAsync(1);
        Assert.Equal("Preview", preview.Status); Assert.Equal(0, device.Writes);
        var target = SwitchboardLayout.FromJson(preview.TargetJson);
        var original = SwitchboardLayout.FromJson(before);
        Assert.Equal(original.Keys[0].Fields, target.Keys[0].Fields);
        Assert.Equal(original.Keys[1].Fields, target.Keys[1].Fields);
        Assert.All(target.Keys.Skip(2), k => Assert.Equal("1", k.Get("Line")));
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Null((await Service().GetStateAsync()).Phones.Single().ConfigurationWarning);
    }

    [Fact]
    public async Task RetiredF2CorrectionsCannotBeAppliedButHistoricalBackupsCanBeRestored()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        await using (var db = factory.CreateDbContext())
        { (await db.SwitchboardDeployments.SingleAsync()).Purpose = "TemplateSetup"; await db.SaveChangesAsync(); }
        var logins = device.Logins;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().DeployAsync(preview.Id));
        Assert.Contains("no longer supported", error.Message);
        Assert.Equal(logins, device.Logins); Assert.Equal(0, device.Writes);
        await using (var db = factory.CreateDbContext())
        {
            (await db.SwitchboardDeployments.SingleAsync()).Status = "Success";
            await db.SaveChangesAsync();
            Assert.Null((await db.SwitchboardProfiles.SingleAsync()).LeaseId);
        }
        var before = device.Layout.Fingerprint();
        var restore = await Service().PreviewRestoreAsync(preview.Id);
        Assert.Equal("Success", (await Service().DeployAsync(restore.Id)).Status);
        Assert.Equal(before, device.Layout.Fingerprint());
    }

    [Fact]
    public async Task AnIncompleteCapturedLayoutStillShowsAnActionableWarning()
    {
        await Service().CaptureAsync(1);
        await using (var db = factory.CreateDbContext())
        {
            var profile = await db.SwitchboardProfiles.SingleAsync();
            var layout = SwitchboardLayout.FromJson(profile.BaselineJson);
            layout.Keys.RemoveAt(59); profile.BaselineJson = layout.ToJson();
            await db.SaveChangesAsync();
        }
        Assert.Contains("five-page, twelve-key", (await Service().GetStateAsync()).Phones.Single().ConfigurationWarning);
        Assert.Equal(0, device.Writes);
    }

    [Theory]
    [InlineData(false, "Cancelled")]
    [InlineData(true, "TimedOut")]
    public async Task CancellationAndTimeoutRetainBackupAndReleaseTheLease(bool timeout, string status)
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        using var cancellation = new CancellationTokenSource();
        device.BeforeWrite = _ => { if (timeout) throw new TimeoutException(); cancellation.Cancel(); };
        var result = await Service().DeployAsync(preview.Id, cancellation.Token);
        Assert.Equal(status, result.Status); Assert.Equal(0, device.Writes);
        await using var db = factory.CreateDbContext();
        var saved = await db.SwitchboardDeployments.SingleAsync();
        Assert.Equal(status, saved.Status); Assert.NotEmpty(saved.BeforeJson); Assert.NotNull(saved.FinishedAt);
        Assert.Null((await db.SwitchboardProfiles.SingleAsync()).LeaseId);
    }

    [Fact]
    public async Task AbandonedDeliveryCanBeRestoredOnlyAfterItsLeaseExpires()
    {
        await ReadyAsync(); var preview = await Service().PreviewAsync(1);
        await using (var db = factory.CreateDbContext())
        {
            (await db.SwitchboardDeployments.SingleAsync()).Status = "Applying";
            (await db.SwitchboardProfiles.SingleAsync()).LeaseUntil = DateTime.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().PreviewRestoreAsync(preview.Id));
        await using (var db = factory.CreateDbContext())
        { (await db.SwitchboardProfiles.SingleAsync()).LeaseUntil = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
        Assert.Equal("Preview", (await Service().PreviewRestoreAsync(preview.Id)).Status);
        Assert.Equal(0, device.Writes);
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    { public AppDbContext CreateDbContext() => new(options); }
    private sealed class Access(bool admin) : IUserAccess
    {
        public Task RequireAuthenticatedAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task RequireAdministratorAsync(CancellationToken ct = default) => admin ? Task.CompletedTask : throw new UnauthorizedAccessException();
    }
}

internal static class SwitchboardFixture
{
    public static SwitchboardLayout Layout(string ip = "192.168.55.133")
    {
        var keys = Enumerable.Range(0, 60).Select(i => new SwitchboardKey(i / 12 + 1, i % 12 + 1,
            new() { ["Type"] = "0", ["Title"] = "", ["Value"] = "", ["MemorySubType"] = "", ["Line"] = "1",
                ["MediaType"] = "", ["PICKUP"] = "", ["IconType"] = "Green", ["KeyeventSubType"] = "", ["ActionURLSubType"] = "0", ["CodecSubType"] = "", ["PttSubType"] = "", ["PttSubTypeValue"] = "", ["LdapSubType"] = "", ["XmlSubType"] = "", ["BWSubType"] = "" })).ToList();
        keys[0].Fields["Type"] = "2";
        keys[1].Fields["Type"] = "1";
        keys[1].Fields["Title"] = ip.EndsWith(".101") ? "Override Day/Night" : "Reception";
        keys[1].Fields["Value"] = ip.EndsWith(".101") ? "*271" : "201";
        keys[1].Fields["MemorySubType"] = ip.EndsWith(".101") ? "" : "91";
        keys[2].Fields["Type"] = keys[3].Fields["Type"] = "1";
        keys[2].Fields["Title"] = "Zulu"; keys[2].Fields["Value"] = "203|0120000000";
        keys[3].Fields["Title"] = "Alpha"; keys[3].Fields["Value"] = "204";
        keys[2].Fields["MemorySubType"] = keys[3].Fields["MemorySubType"] = "91";
        keys[2].Fields["MediaType"] = keys[3].Fields["MediaType"] = "0";
        return new(keys, new() { ["DSS_Key_Home_Page_RW"] = "1", ["CSE_BXferWithDSSKey_RW"] = "91" });
    }
    public static string Html(SwitchboardLayout layout, int page)
    {
        var html = new StringBuilder("<script>var pageNum = '5'; var dsskeyNums = '12';</script><select name='SIDEKEY_CONSOLE_ENTRY'>");
        for (var i = 1; i <= 5; i++) html.Append($"<option value='{i}' {(i == page ? "SELECTED" : "")}>{i}</option>");
        html.Append("</select>");
        foreach (var setting in layout.Settings) html.Append($"<select name='{setting.Key}'><option value='{setting.Value}' selected></option></select>");
        html.Append("<form id='SideKeyForm'>");
        foreach (var key in layout.Keys.Where(k => k.Page == page))
        {
            foreach (var field in key.Fields.Where(f => f.Key != "Line"))
                html.Append($"<input NAME='SIDE_Key_{field.Key}_RW({key.Slot})' VALUE='{WebUtility.HtmlEncode(field.Value)}'>");
            html.Append($"<input id='FUN_Key_Line_Hide({key.Slot})' value='{key.Get("Line")}'>");
        }
        return html.Append("</form>").ToString();
    }
    public sealed class Device : IFanvilClientFactory
    {
        public bool UnsupportedLayout;
        public SwitchboardLayout Layout { get; } = SwitchboardFixture.Layout();
        public Dictionary<string, SwitchboardLayout> Layouts { get; } = [];
        public int Logins, Writes, FailPage;
        public Action<CancellationToken>? BeforeWrite;
        public IFanvilClient Create(string host, bool useTls, TimeSpan? connectTimeout = null) => new Client(this, host);
        private sealed class Client(Device device, string host) : IFanvilClient
        {
            private int page = 1;
            private SwitchboardLayout Layout => device.Layouts.GetValueOrDefault(host, device.Layout);
            public Task<(bool ok, string message)> LoginAsync(string username, string password, CancellationToken ct = default)
            { ct.ThrowIfCancellationRequested(); device.Logins++; return Task.FromResult((true, "OK")); }
            public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
            public Task<FanvilResponse> GetAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<FanvilResponse> DeleteAllContactsAsync(CancellationToken ct = default) => throw new NotSupportedException();
            public Task<FanvilResponse> PostFileAsync(string path, string fieldName, string fileName, byte[] fileBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();
            public Task<FanvilResponse> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
            {
                ct.ThrowIfCancellationRequested();
                if (fields.ContainsKey("DefaultLoad")) page = int.Parse(fields["SIDEKEY_CONSOLE_ENTRY"]);
                else if (fields.ContainsKey("DefaultSubmit"))
                {
                    device.BeforeWrite?.Invoke(ct); ct.ThrowIfCancellationRequested();
                    if (page == device.FailPage) throw new IOException("Synthetic write failure");
                    device.Writes++;
                    foreach (var key in Layout.Keys.Where(k => k.Page == page))
                        foreach (var name in key.Fields.Keys.ToArray()) key.Fields[name] = fields[$"SIDE_Key_{name}_RW({key.Slot})"];
                }
                else throw new InvalidOperationException("Unexpected mutation");
                return Task.FromResult(new FanvilResponse { StatusCode = 200, Body = device.UnsupportedLayout || host == "192.168.55.136" ? "<form>Standard phone with two DSS keys</form>" : Html(Layout, page) });
            }
            public void Dispose() { }
        }
    }
}
