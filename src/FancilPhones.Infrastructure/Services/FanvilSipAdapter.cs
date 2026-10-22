using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FancilPhones.Application;

namespace FancilPhones.Infrastructure.Services;

/// <summary>Fanvil lines.htm protocol. Preserve the captured form and change only reviewed values.</summary>
public sealed class FanvilSipAdapter
{
    private const string Path = "/lines.htm";
    private const string Registration = "SIP_EnableSipReg_RW";
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Dictionary<string, string> Attributes(string tag) => Matches(tag,
        "([\\w-]+)(?:\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+)))?").Cast<Match>()
        .GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Success ? g.First().Groups[2].Value
            : g.First().Groups[3].Success ? g.First().Groups[3].Value : g.First().Groups[4].Value), StringComparer.OrdinalIgnoreCase);

    public sealed record Capture(Dictionary<string, string> Fields, string Fingerprint, SipLineSettings Settings);

    public static Capture Parse(string html, int line)
    {
        if (html.Length > 4 * 1024 * 1024) throw new InvalidOperationException("Unsupported SIP response size.");
        var selector = Matches(html, @"<select\b[^>]*>.*?</select>").Cast<Match>()
            .SingleOrDefault(m => Attributes(m.Value[..(m.Value.IndexOf('>') + 1)]).GetValueOrDefault("name") == "SIP_PhoneLineEntry");
        if (selector is null || SelectedValue(selector.Value) != line.ToString(CultureInfo.InvariantCulture))
            throw new InvalidOperationException("The phone did not return the requested SIP line. No settings were applied.");
        var form = Matches(html, @"<form\b[^>]*>.*?</form>").Cast<Match>()
            .SingleOrDefault(m => Matches(m.Value, @"<input\b[^>]*>").Cast<Match>()
                .Any(i => Attributes(i.Value).GetValueOrDefault("name") == "SIP_DisPlayName_R"))
            ?? throw new InvalidOperationException("The phone did not return a supported SIP form. No settings were applied.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in Matches(form.Value, @"<input\b[^>]*>"))
        {
            var attrs = Attributes(input.Value);
            var name = attrs.GetValueOrDefault("name", "");
            var type = attrs.GetValueOrDefault("type", "text").ToLowerInvariant();
            if (name.Length == 0 || attrs.ContainsKey("disabled") || type is "submit" or "button" or "image" or "reset") continue;
            if (type is "checkbox" or "radio" && !attrs.ContainsKey("checked")) continue;
            // Password masks are deliberately excluded, including from snapshots and fingerprints.
            if (type == "password" || name.Contains("passwd", StringComparison.OrdinalIgnoreCase) || name.Contains("password", StringComparison.OrdinalIgnoreCase)) continue;
            fields.Add(name, attrs.GetValueOrDefault("value", type is "checkbox" or "radio" ? "on" : ""));
        }
        foreach (Match select in Matches(form.Value, @"<select\b[^>]*>.*?</select>"))
        {
            var attrs = Attributes(select.Value[..(select.Value.IndexOf('>') + 1)]);
            var name = attrs.GetValueOrDefault("name", "");
            if (name.Length > 0 && !attrs.ContainsKey("disabled") && name is not ("enable_codec" or "disable_codec"))
                fields.Add(name, SelectedValue(select.Value));
        }
        foreach (var required in EditableFields.Concat(new[] { "CheckBoxManager", "SIP_CodecSets_RW" }))
            if (!fields.ContainsKey(required)) throw new InvalidOperationException("The SIP form is incomplete. No settings were applied.");
        if (!fields["CheckBoxManager"].Split(',').Contains(Registration))
            throw new InvalidOperationException("The handset's SIP activation control is unsupported.");
        if (!int.TryParse(fields["SIP_RegPort_R"], out var port) || !int.TryParse(fields["SIP_BackupPort_R"], out var backupPort))
            throw new InvalidOperationException("The phone returned invalid SIP port settings.");
        var settings = new SipLineSettings
        {
            DisplayName = fields["SIP_DisPlayName_R"], Username = fields["SIP_PhoneNum_R"], AuthenticationUser = fields["SIP_RegUser_R"],
            Realm = fields["SIP_LocalDomain_R"], ServerName = fields["SIP_Name_RW"], ServerAddress = fields["SIP_RegAddr_R"], ServerPort = port,
            BackupServerAddress = fields["SIP_BackupAddr_R"], BackupServerPort = backupPort, RegistrationEnabled = fields.ContainsKey(Registration)
        };
        // Ignore the empty JS-managed line helper. The selector above verifies the actual line.
        fields.Remove("SIP_PhoneLineEntry");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fields.OrderBy(f => f.Key, StringComparer.Ordinal)))));
        return new(fields, fingerprint, settings);
    }

    private static string SelectedValue(string select)
    {
        var options = Matches(select, @"<option\b[^>]*>").Cast<Match>().Select(m => Attributes(m.Value)).ToList();
        return (options.SingleOrDefault(o => o.ContainsKey("selected")) ?? options.FirstOrDefault())?.GetValueOrDefault("value", "") ?? "";
    }

    private static readonly string[] EditableFields = ["SIP_DisPlayName_R", "SIP_PhoneNum_R", "SIP_RegUser_R", "SIP_LocalDomain_R",
        "SIP_Name_RW", "SIP_RegAddr_R", "SIP_RegPort_R", "SIP_BackupAddr_R", "SIP_BackupPort_R"];

    public static Dictionary<string, string> BuildChanges(Capture before, SipLineSettings target)
    {
        Validator.ValidateObject(target, new ValidationContext(target), true);
        if (target.RegistrationEnabled && (string.IsNullOrWhiteSpace(target.Username) || string.IsNullOrWhiteSpace(target.AuthenticationUser)
            || string.IsNullOrWhiteSpace(target.ServerAddress)))
            throw new InvalidOperationException("An active SIP line requires a username, authentication user and primary server address.");
        if (!string.IsNullOrEmpty(target.ReplacementPassword) && target.ReplacementPassword.All(c => c == '*'))
            throw new InvalidOperationException("Enter a replacement password, or leave it blank to preserve the existing password.");
        string[] values = [target.DisplayName, target.Username, target.AuthenticationUser, target.Realm, target.ServerName,
            target.ServerAddress, target.ServerPort.ToString(CultureInfo.InvariantCulture), target.BackupServerAddress, target.BackupServerPort.ToString(CultureInfo.InvariantCulture)];
        var changes = EditableFields.Zip(values).Where(p => before.Fields[p.First] != p.Second)
            .ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
        if (before.Settings.RegistrationEnabled != target.RegistrationEnabled)
        {
            // Only this checkbox may change. A full manager with omitted values clears unrelated settings.
            changes["CheckBoxManager"] = Registration;
            if (target.RegistrationEnabled) changes[Registration] = "ON";
        }
        if (!string.IsNullOrEmpty(target.ReplacementPassword))
            // dialog.js base64encode operates on single-byte character codes, not UTF-8.
            changes["SIP_RegPasswd_R"] = "$EP^%39]" + Convert.ToBase64String(Encoding.Latin1.GetBytes(target.ReplacementPassword));
        return changes;
    }

    public async Task<Capture> ReadAsync(IFanvilClient client, int line, CancellationToken ct)
    {
        if (line is < 1 or > 16) throw new InvalidOperationException("Choose a SIP line between 1 and 16.");
        // DefaultLoad changes only the session's line selection, just like the phone's dropdown.
        var response = await client.PostFormAsync(Path, new Dictionary<string, string>
        { ["ReturnPage"] = Path, ["SIP_PhoneLineEntry"] = line.ToString(CultureInfo.InvariantCulture), ["SIP_PhoneLineTabIndex_R"] = "0", ["DefaultLoad"] = "Load" }, ct);
        EnsureSuccess(response);
        return Parse(response.Body, line);
    }

    public async Task<(bool ok, string message)> ApplyAsync(IFanvilClient client, int line, string fingerprint, SipLineSettings settings, CancellationToken ct)
    {
        var before = await ReadAsync(client, line, ct);
        if (before.Fingerprint != fingerprint) return (false, "SIP settings changed on the phone since they were loaded. Reload and review them before pushing.");
        var changes = BuildChanges(before, settings);
        if (changes.Count == 0) return (true, "No SIP settings changed; nothing was sent to the phone.");
        var expected = new Dictionary<string, string>(before.Fields);
        foreach (var change in changes.Where(c => c.Key is not ("CheckBoxManager" or "SIP_RegPasswd_R"))) expected[change.Key] = change.Value;
        if (!settings.RegistrationEnabled) expected.Remove(Registration);
        // Fanvil requires a complete populated form to commit. Preserve every captured control,
        // including checked options and codec values; never manufacture empty defaults.
        var post = new Dictionary<string, string>(before.Fields);
        foreach (var change in changes) post[change.Key] = change.Value;
        post["CheckBoxManager"] = before.Fields["CheckBoxManager"];
        if (!settings.RegistrationEnabled) post.Remove(Registration);
        post["ReturnPage"] = Path;
        post["SIP_PhoneLineEntry"] = "";
        post["keepList"] = "";
        post["DefaultSubmit"] = "Apply";
        // Never echo masked secrets or codec UI helpers. Never retry this POST.
        EnsureSuccess(await client.PostFormAsync(Path, post, ct));
        var after = await ReadAsync(client, line, ct);
        if (!expected.OrderBy(x => x.Key, StringComparer.Ordinal).SequenceEqual(after.Fields.OrderBy(x => x.Key, StringComparer.Ordinal)))
            return (false, "The SIP read-back did not match the reviewed changes, or unrelated settings changed. Inspect the handset before retrying; changes may have been applied.");
        if (!settings.RegistrationEnabled) return (true, $"Line {line} settings verified; SIP registration is disabled.");
        string status = "Unknown";
        var consecutiveRegistered = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            // Allow re-registration to start; a status read immediately after Apply can still
            // describe the old account. Require two successive registered observations.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            status = await ReadStatusAsync(client, ct);
            consecutiveRegistered = status == "Registered" ? consecutiveRegistered + 1 : 0;
            if (consecutiveRegistered >= 2) return (true, $"Line {line} settings verified; Registered.");
        }
        return (false, $"Line {line} settings were saved and verified, but registration is {status}. Check the SIP account and server before retrying.");
    }

    public async Task<string> ReadStatusAsync(IFanvilClient client, CancellationToken ct)
    {
        var response = await client.GetAsync("/SIP_RegState_R.magicMark?now=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ct);
        if (response.StatusCode != 200 || !int.TryParse(response.Body.Trim(), out var status)) return "Unknown";
        return status switch { 200 => "Registered", 0 => "Inactive", -1 => "Bad server", -60 => "Timeout", < 0 => "System error",
            >= 400 => $"Failed ({status})", _ => "Registering" };
    }

    private static void EnsureSuccess(FanvilResponse response)
    {
        if (response.StatusCode is < 200 or >= 300 || response.Body.Contains("logonButton", StringComparison.OrdinalIgnoreCase)
            || response.Body.Contains("USER_PASSWORD_ERROR", StringComparison.OrdinalIgnoreCase) || response.Body.Contains("Invalid Value", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The phone rejected the SIP request. Check its connection and line configuration; changes may have been applied if Apply was sent.");
    }
}
