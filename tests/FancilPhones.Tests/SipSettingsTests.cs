using System.Net;
using FancilPhones.Application;
using FancilPhones.Infrastructure.Services;

namespace FancilPhones.Tests;

public sealed class SipSettingsTests
{
    [Fact]
    public async Task NameChangePreservesCredentialsCheckboxesCodecsAndOtherLines()
    {
        var device = new SipDevice();
        var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 2, default);
        var desired = before.Settings.Copy(); desired.DisplayName = "Desk & <Sales> \"2\"";
        var result = await adapter.ApplyAsync(client, 2, before.Fingerprint, desired, default);
        Assert.True(result.ok, result.message);
        Assert.Equal("Registered", await adapter.ReadStatusAsync(client, default));
        Assert.Equal("original-secret", device.Secret);
        Assert.Equal("auth-different-from-extension", device.Lines[2]["SIP_RegUser_R"]);
        Assert.Equal("ON", device.Lines[2]["SIP_EnableKeepAuth_RW"]);
        Assert.Equal("ON", device.Lines[2]["SIP_EnableViaRport_RW"]);
        Assert.Equal("G729,PCMU,PCMA", device.Lines[2]["SIP_CodecSets_RW"]);
        Assert.Equal("Original & Desk", device.Lines[1]["SIP_DisPlayName_R"]);
        Assert.Equal(desired.DisplayName, (await adapter.ReadAsync(client, 2, default)).Settings.DisplayName);
        var post = Assert.Single(device.Applies);
        Assert.DoesNotContain("SIP_RegPasswd_R", post.Keys);
        Assert.DoesNotContain("SIP_ProxyPswd_R", post.Keys);
        Assert.Equal("", post["SIP_PhoneLineEntry"]);
    }

    [Fact]
    public async Task ExtensionDoesNotImplicitlyReplaceAuthenticationUser()
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.Username = "999";
        Assert.True((await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default)).ok);
        Assert.Equal("999", device.Lines[1]["SIP_PhoneNum_R"]);
        Assert.Equal(before.Settings.AuthenticationUser, device.Lines[1]["SIP_RegUser_R"]);
    }

    [Theory]
    [InlineData("new-secret", "$EP^%39]bmV3LXNlY3JldA==")]
    [InlineData("caf\u00e9", "$EP^%39]Y2Fm6Q==")]
    public async Task ExplicitPasswordUsesObservedEncodingAndIsAbsentFromCapture(string password, string encoded)
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        Assert.Null(before.Settings.ReplacementPassword);
        Assert.DoesNotContain(before.Fields.Keys, k => k.Contains("Passwd") || k.Contains("Pswd"));
        var desired = before.Settings.Copy(); desired.ReplacementPassword = password;
        Assert.True((await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default)).ok);
        Assert.Equal(encoded, Assert.Single(device.Applies)["SIP_RegPasswd_R"]);
        Assert.Equal(password, device.Secret);
    }

    [Fact]
    public async Task DisablingRegistrationPreservesOtherCheckboxes()
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.RegistrationEnabled = false;
        Assert.True((await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default)).ok);
        Assert.DoesNotContain("SIP_EnableSipReg_RW", device.Lines[1].Keys);
        Assert.Equal("ON", device.Lines[1]["SIP_EnableKeepAuth_RW"]);
        Assert.DoesNotContain("SIP_EnableStun_RW", device.Lines[1].Keys);
        var disabled = await adapter.ReadAsync(client, 1, default);
        desired.RegistrationEnabled = true;
        Assert.True((await adapter.ApplyAsync(client, 1, disabled.Fingerprint, desired, default)).ok);
        Assert.Equal("ON", device.Lines[1]["SIP_EnableSipReg_RW"]);
    }

    [Fact]
    public async Task PhysicalDriftStopsApplyBeforeAnyWrite()
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        device.Lines[1]["SIP_RegAddr_R"] = "changed.example";
        var desired = before.Settings.Copy(); desired.DisplayName = "Changed";
        var result = await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default);
        Assert.False(result.ok); Assert.Contains("Reload", result.message); Assert.Empty(device.Applies);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Http200CannotHideSilentRejectionOrUnrelatedChanges(bool ignoreApply, bool corrupt)
    {
        var device = new SipDevice { IgnoreApply = ignoreApply, CorruptUnrelatedSetting = corrupt };
        var adapter = new FanvilSipAdapter(); using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.DisplayName = "Changed";
        var result = await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default);
        Assert.False(result.ok); Assert.Contains("read-back", result.message); Assert.Single(device.Applies);
    }

    [Theory]
    [InlineData(403, "Failed (403)")]
    [InlineData(-60, "Timeout")]
    public async Task RegistrationFailureCannotBeReportedAsSuccess(int status, string description)
    {
        var device = new SipDevice { Status = status }; var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.DisplayName = "Changed";
        var result = await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default);
        Assert.False(result.ok); Assert.Contains(description, result.message); Assert.Single(device.Applies);
    }

    [Fact]
    public async Task OneStaleRegisteredResponseCannotHideSubsequentRegistrationFailure()
    {
        var device = new SipDevice { Status = 403 };
        device.RegistrationStates.Enqueue(200);
        var adapter = new FanvilSipAdapter(); using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.DisplayName = "Changed";
        var result = await adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default);
        Assert.False(result.ok); Assert.Contains("Failed (403)", result.message);
    }

    [Fact]
    public async Task NoChangesDoNotSendApply()
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter(); using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        Assert.True((await adapter.ApplyAsync(client, 1, before.Fingerprint, before.Settings, default)).ok);
        Assert.Empty(device.Applies);
    }

    [Fact]
    public async Task InvalidOrMaskedReplacementDoesNotSendApply()
    {
        var device = new SipDevice(); var adapter = new FanvilSipAdapter(); using var client = device.Create("test", false);
        var before = await adapter.ReadAsync(client, 1, default);
        var desired = before.Settings.Copy(); desired.ReplacementPassword = "********************************";
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default));
        desired.ReplacementPassword = "unsupported-\u4e2d";
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() => adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default));
        desired.ReplacementPassword = null; desired.ServerAddress = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ApplyAsync(client, 1, before.Fingerprint, desired, default));
        Assert.Empty(device.Applies);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("incomplete")]
    [InlineData("wrong-line")]
    public async Task UnsupportedResponsesStopBeforeWrite(string response)
    {
        var device = new SipDevice { ResponseMode = response }; var adapter = new FanvilSipAdapter();
        using var client = device.Create("test", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ReadAsync(client, 2, default));
        Assert.Empty(device.Applies);
    }
}

// A populated multi-form handset response, with the server's checkbox-manager semantics.
internal sealed class SipDevice : IFanvilClientFactory
{
    public Dictionary<int, Dictionary<string, string>> Lines { get; } = Enumerable.Range(1, 2).ToDictionary(i => i, _ => new Dictionary<string, string>
    {
        ["SIP_DisPlayName_R"] = "Original & Desk", ["SIP_PhoneNum_R"] = "236", ["SIP_RegUser_R"] = "auth-different-from-extension",
        ["SIP_LocalDomain_R"] = "", ["SIP_Name_RW"] = "", ["SIP_RegAddr_R"] = "pbx.example", ["SIP_RegPort_R"] = "5060",
        ["SIP_BackupAddr_R"] = "", ["SIP_BackupPort_R"] = "5060", ["SIP_CodecSets_RW"] = "G729,PCMU,PCMA",
        ["CheckBoxManager"] = "SIP_EnableSipReg_RW,SIP_EnableKeepAuth_RW,SIP_EnableViaRport_RW,SIP_EnableStun_RW",
        ["SIP_EnableSipReg_RW"] = "ON", ["SIP_EnableKeepAuth_RW"] = "ON", ["SIP_EnableViaRport_RW"] = "ON", ["SIP_Transport_RW"] = "1"
    });
    public List<Dictionary<string, string>> Applies { get; } = [];
    public string Secret { get; private set; } = "original-secret";
    public bool IgnoreApply { get; init; }
    public bool CorruptUnrelatedSetting { get; init; }
    public string? ResponseMode { get; init; }
    public int Status { get; init; } = 200;
    public Queue<int> RegistrationStates { get; } = new();
    public Action? OnApply { get; init; }
    public IFanvilClient Create(string host, bool useTls, TimeSpan? connectTimeout = null) => new Client(this);
    private sealed class Client(SipDevice device) : IFanvilClient
    {
        private int line = 1;
        public Task<(bool ok, string message)> LoginAsync(string username, string password, CancellationToken ct = default) => Task.FromResult((true, "OK"));
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
        public Task<FanvilResponse> GetAsync(string path, CancellationToken ct = default) => Task.FromResult(new FanvilResponse { StatusCode = 200, Body = (device.RegistrationStates.TryDequeue(out var status) ? status : device.Status).ToString() });
        public Task<FanvilResponse> PostFormAsync(string path, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (fields.ContainsKey("DefaultLoad")) line = int.Parse(fields["SIP_PhoneLineEntry"]);
            if (fields.ContainsKey("DefaultSubmit"))
            {
                device.Applies.Add(new(fields));
                if (!device.IgnoreApply)
                {
                    var state = device.Lines[line];
                    // Mirror a handset that silently ignores incomplete Apply requests.
                    if (fields.ContainsKey("SIP_CodecSets_RW") && fields.ContainsKey("SIP_RegAddr_R"))
                    {
                        foreach (var checkbox in fields.GetValueOrDefault("CheckBoxManager", "").Split(','))
                            if (!fields.ContainsKey(checkbox)) state.Remove(checkbox);
                        foreach (var field in fields.Where(f => state.ContainsKey(f.Key) || f.Key == "SIP_EnableSipReg_RW")) state[field.Key] = field.Value;
                        if (fields.TryGetValue("SIP_RegPasswd_R", out var secret))
                            device.Secret = System.Text.Encoding.Latin1.GetString(Convert.FromBase64String(secret[8..]));
                        if (device.CorruptUnrelatedSetting) state["SIP_CodecSets_RW"] = "PCMA";
                    }
                }
            }
            if (fields.ContainsKey("DefaultSubmit")) device.OnApply?.Invoke();
            if (device.ResponseMode == "login") return Task.FromResult(new FanvilResponse { StatusCode = 200, Body = "logonButton" });
            var selected = device.ResponseMode == "wrong-line" ? 1 : line;
            var html = "<form><select name='SIP_PhoneLineEntry'>" + string.Join("", Enumerable.Range(1, 2).Select(i => $"<option value='{i}' {(i == selected ? "selected" : "")}>Line {i}</option>")) + "</select></form><form>";
            foreach (var field in device.Lines[line])
            {
                if (device.ResponseMode == "incomplete" && field.Key == "SIP_CodecSets_RW") continue;
                var checkbox = field.Key.StartsWith("SIP_Enable");
                html += $"<input name='{field.Key}' type='{(checkbox ? "checkbox" : "text")}' value='{WebUtility.HtmlEncode(field.Value)}' {(checkbox ? "CHECKED" : "")}>";
            }
            html += "<input type='checkbox' name='SIP_EnableStun_RW' value='ON'><input type='password' name='SIP_RegPasswd_R' value='********************************'><input type='password' name='SIP_ProxyPswd_R' value='********************************'><input disabled name='uiOnly' value='ignore'></form>";
            return Task.FromResult(new FanvilResponse { StatusCode = 200, Body = html });
        }
        public Task<FanvilResponse> DeleteAllContactsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FanvilResponse> PostFileAsync(string path, string fieldName, string fileName, byte[] fileBytes, string fileContentType, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
