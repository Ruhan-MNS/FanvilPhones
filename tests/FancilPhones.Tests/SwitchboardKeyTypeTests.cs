using FancilPhones.Application;

namespace FancilPhones.Tests;

public sealed partial class SwitchboardServiceTests
{
    [Fact]
    public async Task AnExplicitLineOnF1DoesNotReplaceThePrimaryLineOnOtherKeysDuringLaterSync()
    {
        await Service().CaptureAsync(1); var reception = await AddReceptionAsync();
        var editor = await Service().GetEditorAsync(1); editor.Layout.Keys[0].Fields["Line"] = "7";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        Assert.Equal("Success", (await Service().DeployAsync((await Service().PreviewDraftAsync(1, 1, true)).Id)).Status);
        var preview = await Service().PreviewDraftAsync(1, 3, false);
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Equal("7", reception.Keys[0].Get("Line"));
        Assert.All(reception.Keys.Skip(1), k => Assert.Equal("2", k.Get("Line")));
    }

    [Theory]
    [InlineData("0", "None", "", "")]
    [InlineData("1", "Memory Key", "MemorySubType", "91")]
    [InlineData("2", "Line", "", "")]
    [InlineData("3", "Key Event", "KeyeventSubType", "20")]
    [InlineData("4", "DTMF", "", "")]
    [InlineData("7", "URL", "", "")]
    [InlineData("13", "BLF List Key", "", "")]
    [InlineData("14", "MCAST Paging", "CodecSubType", "PCMU")]
    [InlineData("20", "Action URL", "ActionURLSubType", "0")]
    [InlineData("21", "XML Browser", "", "")]
    [InlineData("22", "MCAST Listening", "", "")]
    [InlineData("24", "PTT", "PttSubType", "25")]
    public async Task EveryHandsetTypeCanBeEditedOnF1DeployedReadBackAndRestored(string type, string label, string field, string subtype)
    {
        await Service().CaptureAsync(1); var original = device.Layout.Fingerprint();
        var editor = await Service().GetEditorAsync(1); var key = editor.Layout.Keys[0];
        SwitchboardKeyOptions.ChangeType(key, type, "1"); key.Fields["Title"] = "Custom F1";
        if (SwitchboardKeyOptions.HasValue(key)) key.Fields["Value"] = type is "7" or "20" or "21" ? "https://phone.example/configuration/long-path-over-thirty-one-bytes" : type is "14" or "22" ? "239.0.0.1:5000" : "210";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        var preview = await Service().PreviewDraftAsync(1, 1, true);
        Assert.Equal(0, device.Writes); Assert.Equal(type, SwitchboardLayout.FromJson(preview.TargetJson).Keys[0].Get("Type"));
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Equal(type, device.Layout.Keys[0].Get("Type")); Assert.Equal(label, device.Layout.Keys[0].TypeLabel);
        if (field.Length > 0) Assert.Equal(subtype, device.Layout.Keys[0].Get(field));
        var restore = await Service().PreviewRestoreAsync(preview.Id);
        Assert.Equal("Success", (await Service().DeployAsync(restore.Id)).Status);
        Assert.Equal(original, device.Layout.Fingerprint());
    }

    [Fact]
    public async Task F1AndF2EditsSyncToAnotherPhoneIncludingReceptionWithoutAutomaticCorrection()
    {
        await Service().CaptureAsync(1); var reception = await AddReceptionAsync();
        var editor = await Service().GetEditorAsync(1);
        SwitchboardKeyOptions.ChangeType(editor.Layout.Keys[0], "3", "1");
        SwitchboardKeyOptions.ChangeSubtype(editor.Layout.Keys[0], "1"); editor.Layout.Keys[0].Fields["Title"] = "Do not disturb";
        editor.Layout.Keys[1].Fields["Title"] = "Support"; editor.Layout.Keys[1].Fields["Value"] = "299|0123456789";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        var preview = await Service().PreviewDraftAsync(1, 3, false);
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Equal("3", reception.Keys[0].Get("Type")); Assert.Equal("1", reception.Keys[0].Get("KeyeventSubType"));
        Assert.Equal("Support", reception.Keys[1].Name); Assert.Equal("299|0123456789", reception.Keys[1].Get("Value"));
        Assert.All(reception.Keys, k => Assert.Equal("2", k.Get("Line")));
        Assert.Null((await Service().GetStateAsync()).Phones.Single(p => p.Id == 3).ConfigurationWarning);
        var restore = await Service().PreviewRestoreAsync(preview.Id);
        Assert.Equal("Success", (await Service().DeployAsync(restore.Id)).Status);
        Assert.Equal("2", reception.Keys[0].Get("Type")); Assert.Equal("*271", reception.Keys[1].Extension);
    }

    [Theory]
    [InlineData("3", "60", "LdapSubType", "4")]
    [InlineData("3", "61", "XmlSubType", "2")]
    [InlineData("3", "62", "BWSubType", "6")]
    [InlineData("24", "14", "PttSubTypeValue", "PCMA")]
    public async Task NestedEventAndPttFieldsSurviveTheProtocolRoundTrip(string type, string subtype, string detail, string value)
    {
        await Service().CaptureAsync(1); var editor = await Service().GetEditorAsync(1); var key = editor.Layout.Keys[1];
        SwitchboardKeyOptions.ChangeType(key, type, "1"); SwitchboardKeyOptions.ChangeSubtype(key, subtype);
        key.Fields[detail] = value;
        if (SwitchboardKeyOptions.HasValue(key)) key.Fields["Value"] = "239.0.0.1:5000";
        await Service().SaveDraftAsync(1, editor.Revision, editor.Layout, false);
        var preview = await Service().PreviewDraftAsync(1, 1, true);
        Assert.Equal("Success", (await Service().DeployAsync(preview.Id)).Status);
        Assert.Equal(value, device.Layout.Keys[1].Get(detail));
        var posted = FancilPhones.Infrastructure.Services.FanvilSwitchboardAdapter.BuildFields(device.Layout.Keys.Take(12));
        Assert.Equal(value, posted[$"SIDE_Key_{detail}_RW(2)"]);
    }
}
