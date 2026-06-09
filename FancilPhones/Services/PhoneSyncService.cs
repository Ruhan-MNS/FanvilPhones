using System.Text.RegularExpressions;
using FancilPhones.Data;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Services;

public record SyncResult(int PhoneId, string PhoneName, bool Success, int? HttpStatus, string Message);

public class PhoneSyncService
{
    private readonly IDbContextFactory<AppDbContext> _dbf;
    private readonly ILogger<PhoneSyncService> _log;

    public PhoneSyncService(IDbContextFactory<AppDbContext> dbf, ILogger<PhoneSyncService> log)
    {
        _dbf = dbf;
        _log = log;
    }

    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var phones = await db.Phones.Where(p => p.Enabled).ToListAsync(ct);
        var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync(ct);
        var csv = PhonebookCsv.Build(contacts);

        var tasks = phones.Select(p => PushAsync(p, contacts.Count, csv, ct)).ToList();
        return await Task.WhenAll(tasks);
    }

    public async Task<SyncResult> SyncOneAsync(int phoneId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.FindAsync(new object?[] { phoneId }, ct);
        if (phone is null) return new SyncResult(phoneId, "?", false, null, "Phone not found");
        var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync(ct);
        var csv = PhonebookCsv.Build(contacts);
        return await PushAsync(phone, contacts.Count, csv, ct);
    }

    private async Task<SyncResult> PushAsync(Phone phone, int contactCount, byte[] csv, CancellationToken ct)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var run = new SyncRun
        {
            PhoneId = phone.Id,
            StartedAt = DateTime.UtcNow,
            ContactCount = contactCount,
            Status = "Running",
            Action = "Sync",
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            using var client = new FanvilHttpClient(phone.IpAddress, phone.Scheme == "https");

            // Step 1: nonce/MD5 login to obtain the session ('auth') cookie.
            var login = await client.LoginAsync(phone.Username, phone.Password, ct);
            if (!login.ok)
                throw new Exception(login.message);

            // Step 2: wipe the phone's existing phonebook then upload the master
            // CSV - the phone's import only merges, so a delete-all first makes the
            // sync a true mirror (deletions in the app propagate). Finally log out
            // so the phone releases its (limited) session slot.
            FanvilResponse resp;
            try
            {
                await client.DeleteAllContactsAsync(ct);
                resp = await client.PostFileAsync(
                    phone.UploadPath, phone.UploadFieldName, "phonebook.csv",
                    csv, "application/octet-stream", ct);
            }
            finally
            {
                await client.LogoutAsync(ct);
            }

            var ok = resp.StatusCode is >= 200 and < 400;
            var msg = ok
                ? $"Uploaded {contactCount} contacts (HTTP {resp.StatusCode})"
                : $"HTTP {resp.StatusCode}: {Truncate(resp.Body, 400)}";

            run.FinishedAt = DateTime.UtcNow;
            run.Status = ok ? "Success" : "Failed";
            run.HttpStatusCode = resp.StatusCode;
            run.Message = msg;

            phone.LastSyncedAt = run.FinishedAt;
            phone.LastSyncStatus = run.Status;
            phone.LastSyncMessage = msg;
            db.Phones.Update(phone);
            await db.SaveChangesAsync(ct);

            return new SyncResult(phone.Id, phone.Name, ok, resp.StatusCode, msg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Sync failed for {Phone} ({Ip})", phone.Name, phone.IpAddress);
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "Error";
            run.Message = ex.Message;

            phone.LastSyncedAt = run.FinishedAt;
            phone.LastSyncStatus = "Error";
            phone.LastSyncMessage = ex.Message;
            db.Phones.Update(phone);
            await db.SaveChangesAsync(ct);

            return new SyncResult(phone.Id, phone.Name, false, null, ex.Message);
        }
    }

    /// <summary>
    /// Pushes <see cref="Phone.SipDisplayName"/> into the phone's SIP line
    /// settings (<c>SIP_DisPlayName_R</c> on <c>/lines.htm</c>) for the line
    /// indicated by <see cref="Phone.SipLineIndex"/>. Phonebook sync is NOT
    /// affected — this is a separate action.
    ///
    /// Safety: the phone's <c>/lines.htm</c> form echoes saved passwords as a
    /// masked obfuscated string (e.g. <c>$EP^%39]KioqKioq...</c>). POSTing those
    /// back would overwrite the real SIP password. We therefore submit only the
    /// display-name field (plus the line selector / ReturnPage hidden fields)
    /// and explicitly skip anything matching <c>passwd</c>/<c>password</c>.
    /// </summary>
    public async Task<(bool ok, string message)> PushSipDisplayNameAsync(int phoneId, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.FindAsync(new object?[] { phoneId }, ct);
        if (phone is null) return (false, "Phone not found");

        var displayName = phone.SipDisplayName?.Trim();
        var extension = phone.SipExtension?.Trim();
        if (string.IsNullOrEmpty(displayName) && string.IsNullOrEmpty(extension))
            return (false, "No SIP display name or extension configured for this phone.");

        var run = new SyncRun
        {
            PhoneId = phone.Id,
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            Action = "Push",
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Multi-line support not yet implemented; line selector reverse-engineering
        // is a follow-up. Clamp to line 1 and surface this in the message.
        var requestedLine = phone.SipLineIndex;
        var lineIdx = 1;
        var lineNote = requestedLine > 1
            ? $" (requested line {requestedLine}; multi-line not yet supported, pushed to line 1)"
            : "";

        try
        {
            using var client = new FanvilHttpClient(phone.IpAddress, phone.Scheme == "https");

            var login = await client.LoginAsync(phone.Username, phone.Password, ct);
            if (!login.ok)
                return (false, login.message);

            try
            {
                // GET the line settings form so we can echo every field back.
                // The phone's POST handler silently no-ops if required fields are
                // missing, so a minimum-field POST does not work — we mirror what
                // the browser sends, swapping only SIP_DisPlayName_R and excluding
                // password fields (their values are masked echoes).
                var get = await client.GetAsync("/lines.htm", ct);

                // /lines.htm contains multiple <form> blocks (line-selector,
                // SIP-config, etc.). Pick the one that owns SIP_DisPlayName_R
                // and echo only that form's fields back — same as the browser.
                List<FormField>? sipFormFields = null;
                foreach (var formBody in ExtractFormBlocks(get.Body))
                {
                    var ff = ExtractFormFields(formBody);
                    if (ff.Any(f => f.Name == "SIP_DisPlayName_R"))
                    {
                        sipFormFields = ff;
                        break;
                    }
                }
                if (sipFormFields is null || sipFormFields.Count == 0)
                    return (false, "Could not locate the SIP form on the phone " +
                                   "(no <form> contains SIP_DisPlayName_R). Aborted.");

                var currentName = sipFormFields.First(f => f.Name == "SIP_DisPlayName_R").Value;
                var currentExt = sipFormFields.FirstOrDefault(f => f.Name == "SIP_PhoneNum_R")?.Value ?? "";

                // The /lines.htm form is populated by JavaScript (the static HTML
                // has empty `value` attributes; real values arrive via the page's
                // magicMark XHRs). Our parser only sees the static HTML, so an
                // "echo every field back" POST submits empty for every field
                // EXCEPT the ones we explicitly override — wiping the phone's
                // SIP config. To avoid that, we send ONLY the fields we want to
                // change, plus the minimum state Fanvil needs to commit:
                //   DefaultSubmit=Apply (action selector)
                //   CheckBoxManager (preserves checkbox-section state)
                //   keepList= (codec retention, JS-added but expected empty)
                //   SIP_PhoneLineEntry (preserved from form parse)
                // Fanvil's form handler preserves fields NOT included in POST.
                var post = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var f in sipFormFields)
                {
                    if (!ExtraStateFields.Contains(f.Name)) continue;
                    if (ExcludedFields.Contains(f.Name)) continue;
                    post[f.Name] = f.Value;
                }
                if (!post.ContainsKey("keepList")) post["keepList"] = "";
                post["DefaultSubmit"] = "Apply";

                if (!string.IsNullOrEmpty(displayName))
                    post["SIP_DisPlayName_R"] = displayName;
                if (!string.IsNullOrEmpty(extension))
                {
                    // The phone's UI labels these "Username" and "Authentication
                    // User"; both normally hold the same extension.
                    post["SIP_PhoneNum_R"] = extension;
                    post["SIP_RegUser_R"] = extension;
                }
                // "Activate" checkbox on the line settings page. Per Fanvil
                // convention, checked → field present with value 1; unchecked →
                // field omitted (CheckBoxManager declares it exists so the
                // server treats absent as unchecked).
                if (phone.SipRegistrationEnabled)
                    post["SIP_EnableSipReg_RW"] = "ON";

                _log.LogInformation(
                    "PushSipLine: phone {Phone} ({Ip}) — name='{Cn}'->'{Tn}', ext='{Ce}'->'{Te}', POSTing {Count} fields.",
                    phone.Name, phone.IpAddress,
                    currentName, displayName ?? "(unchanged)",
                    currentExt, extension ?? "(unchanged)",
                    post.Count);
                // Dump the full POST body so it can be diffed against the browser's
                // Apply payload. Length-prefixed because some lines are long.
                var encodedBody = string.Join("&", post.Select(kv =>
                    $"{Uri.EscapeDataString(kv.Key).Replace("%20", "+")}=" +
                    $"{Uri.EscapeDataString(kv.Value).Replace("%20", "+")}"));
                _log.LogInformation("PushSipLine POST body ({Len} bytes): {Body}",
                    encodedBody.Length, encodedBody);

                var resp = await client.PostFormAsync("/lines.htm", post, ct);
                var body = resp.Body ?? "";
                _log.LogInformation("PushSipLine POST response (HTTP {Status}, {Len} bytes): {Snippet}",
                    resp.StatusCode, body.Length, Truncate(body, 500));

                var bouncedToLogin = body.Contains("logonButton", StringComparison.OrdinalIgnoreCase)
                                     || body.Contains("USER_PASSWORD_ERROR", StringComparison.OrdinalIgnoreCase);
                var invalidValue = body.Contains("Invalid Value", StringComparison.OrdinalIgnoreCase);
                if (resp.StatusCode is < 200 or >= 400 || bouncedToLogin || invalidValue)
                {
                    var fail = invalidValue
                        ? "Phone returned 'Invalid Value'. The handset's SIP config likely has " +
                          "empty/invalid required fields (often from a previous push that wiped them) " +
                          "and refuses any form submission until manually repaired. Open the phone's " +
                          "web UI, fix the line settings, and click Apply there first."
                        : $"POST failed: HTTP {resp.StatusCode}: {Truncate(body, 200)}";
                    phone.LastSyncedAt = DateTime.UtcNow;
                    phone.LastSyncStatus = "Failed";
                    phone.LastSyncMessage = fail;
                    run.FinishedAt = DateTime.UtcNow;
                    run.Status = "Failed";
                    run.HttpStatusCode = resp.StatusCode;
                    run.Message = fail;
                    db.Phones.Update(phone);
                    await db.SaveChangesAsync(ct);
                    return (false, fail);
                }

                // Verify: parse the POST response itself (it's the re-rendered
                // lines.htm page). Doing a separate GET would race the phone's
                // apply commit and risk reading a stale page.
                string actualName = "", actualExt = "";
                bool foundForm = false;
                foreach (var formBody in ExtractFormBlocks(body))
                {
                    var ff = ExtractFormFields(formBody);
                    if (ff.Any(f => f.Name == "SIP_DisPlayName_R"))
                    {
                        actualName = ff.First(f => f.Name == "SIP_DisPlayName_R").Value;
                        actualExt = ff.FirstOrDefault(f => f.Name == "SIP_PhoneNum_R")?.Value ?? "";
                        foundForm = true;
                        break;
                    }
                }

                // If the POST response doesn't contain the line form (e.g. the
                // page is JS-populated and we can't read the value from static
                // HTML), treat that as a soft success — POST returned 200, no
                // Invalid Value, no login bounce. We've done all we can verify.
                var nameOk = string.IsNullOrEmpty(displayName) || !foundForm ||
                             string.Equals(actualName, displayName, StringComparison.Ordinal);
                var extOk = string.IsNullOrEmpty(extension) || !foundForm ||
                            string.Equals(actualExt, extension, StringComparison.Ordinal);
                var ok = nameOk && extOk;

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(displayName))
                    parts.Add(nameOk
                        ? $"display name = '{displayName}'"
                        : $"display name still '{actualName}' (expected '{displayName}')");
                if (!string.IsNullOrEmpty(extension))
                    parts.Add(extOk
                        ? $"username = '{extension}'"
                        : $"username still '{actualExt}' (expected '{extension}')");
                if (phone.SipRegistrationEnabled)
                    parts.Add("registration enabled");

                var verifyNote = !foundForm
                    ? " (response page didn't include the form — change accepted but couldn't verify)"
                    : "";
                var msg = ok
                    ? $"Pushed to line {lineIdx}: {string.Join(", ", parts)}{lineNote}{verifyNote}"
                    : $"Phone silently rejected part of the push — {string.Join("; ", parts)}";

                phone.LastSyncedAt = DateTime.UtcNow;
                phone.LastSyncStatus = ok ? "Success" : "Failed";
                phone.LastSyncMessage = msg;
                run.FinishedAt = DateTime.UtcNow;
                run.Status = ok ? "Success" : "Failed";
                run.HttpStatusCode = resp.StatusCode;
                run.Message = msg;
                db.Phones.Update(phone);
                await db.SaveChangesAsync(ct);

                return (ok, msg);
            }
            finally
            {
                await client.LogoutAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Push display name failed for {Phone} ({Ip})",
                phone.Name, phone.IpAddress);
            phone.LastSyncedAt = DateTime.UtcNow;
            phone.LastSyncStatus = "Error";
            phone.LastSyncMessage = ex.Message;
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "Error";
            run.Message = ex.Message;
            db.Phones.Update(phone);
            await db.SaveChangesAsync(ct);
            return (false, ex.Message);
        }
    }

    private static readonly Regex FormBlockRx = new(
        "<form\\b(?<attrs>[^>]*)>(?<body>.*?)</form>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex InputTagRx = new(
        "<input\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SelectBlockRx = new(
        "<select\\b(?<attrs>[^>]*)>(?<body>.*?)</select>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex OptionTagRx = new(
        "<option\\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(
        "(?<k>[\\w-]+)\\s*=\\s*(\"(?<v>[^\"]*)\"|'(?<v>[^']*)'|(?<v>[^\\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareAttrRx = new(
        "(?<![\\w-])(?<k>checked|selected|disabled)(?![\\w-=])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private record FormField(string Name, string Value, string Type);

    /// <summary>
    /// Non-SIP_-prefixed state fields the Apply button's POST also includes —
    /// extracted from a real browser network trace. <c>DefaultSubmit=Apply</c>
    /// is the action selector; <c>CheckBoxManager</c> declares which checkboxes
    /// are present on the page; <c>keepList</c> is the codec retention list
    /// (usually empty on a normal Apply); <c>SIP_PhoneLineEntry</c> is the line
    /// selector. Everything else stays scoped to the SIP_* convention.
    /// </summary>
    private static readonly HashSet<string> ExtraStateFields = new(StringComparer.Ordinal)
    {
        "DefaultSubmit", "CheckBoxManager", "keepList", "SIP_PhoneLineEntry",
    };

    /// <summary>
    /// Fields that appear in the page's HTML but the browser deliberately
    /// excludes from the Apply POST (they're UI-only / managed by JS).
    /// </summary>
    private static readonly HashSet<string> ExcludedFields = new(StringComparer.Ordinal)
    {
        "disable_codec", "enable_codec", "SIP_Preview_Mode_RW",
    };

    /// <summary>
    /// Returns the inner body of every &lt;form&gt; block in the document, in
    /// document order. Fanvil's /lines.htm page has several independent forms
    /// (line-selector, SIP config, etc.) and we must avoid cross-form mixing
    /// when we echo fields back — the browser only submits ONE form per click.
    /// </summary>
    private static List<string> ExtractFormBlocks(string body)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(body)) return result;
        foreach (Match m in FormBlockRx.Matches(body))
            result.Add(m.Groups["body"].Value);
        // If the document has no <form>...</form> at all (some firmwares render
        // the line form inline without explicit tags), fall back to the whole body.
        if (result.Count == 0) result.Add(body);
        return result;
    }

    /// <summary>
    /// Scans an HTML body for form fields (&lt;input&gt; of any type except
    /// password, plus &lt;select&gt; with its selected option) and returns
    /// them with their type so the caller can choose which to submit.
    /// </summary>
    private static List<FormField> ExtractFormFields(string body)
    {
        var result = new List<FormField>();
        if (string.IsNullOrEmpty(body)) return result;

        // <input> of every type except password. For checkbox/radio, only emit
        // when 'checked' is present (otherwise the browser omits the field).
        foreach (Match m in InputTagRx.Matches(body))
        {
            var attrs = ParseAttrs(m.Value);
            if (!attrs.TryGetValue("name", out var name) || string.IsNullOrEmpty(name)) continue;
            var type = attrs.GetValueOrDefault("type", "text").ToLowerInvariant();
            if (type == "password") continue;
            if (LooksLikePassword(name)) continue;
            if (type is "button" or "image" or "reset") continue;
            if (type is "checkbox" or "radio")
            {
                if (!attrs.ContainsKey("checked")) continue;
                result.Add(new FormField(name, attrs.GetValueOrDefault("value", "on"), type));
                continue;
            }
            result.Add(new FormField(name, attrs.GetValueOrDefault("value", ""), type));
        }

        // <select name=...>: pick the <option selected> value; if none selected,
        // browsers send the first option's value.
        foreach (Match sm in SelectBlockRx.Matches(body))
        {
            var selAttrs = ParseAttrs("<select " + sm.Groups["attrs"].Value + ">");
            if (!selAttrs.TryGetValue("name", out var name) || string.IsNullOrEmpty(name)) continue;
            if (LooksLikePassword(name)) continue;

            string? selectedValue = null;
            string? firstValue = null;
            foreach (Match om in OptionTagRx.Matches(sm.Groups["body"].Value))
            {
                var oAttrs = ParseAttrs(om.Value);
                var val = oAttrs.GetValueOrDefault("value", "");
                firstValue ??= val;
                if (oAttrs.ContainsKey("selected")) { selectedValue = val; break; }
            }
            result.Add(new FormField(name, selectedValue ?? firstValue ?? "", "select"));
        }

        return result;
    }

    private static Dictionary<string, string> ParseAttrs(string tag)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in AttrRx.Matches(tag))
            d[a.Groups["k"].Value] = a.Groups["v"].Value;
        // Bare boolean attributes (no =value).
        foreach (Match a in BareAttrRx.Matches(tag))
        {
            var k = a.Groups["k"].Value;
            if (!d.ContainsKey(k)) d[k] = "";
        }
        return d;
    }

    private static bool LooksLikePassword(string name) =>
        name.Contains("passwd", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase);

    public async Task<(bool reachable, int? status, string message)> ProbeAsync(
        Phone phone, CancellationToken ct = default)
    {
        await using var db = await _dbf.CreateDbContextAsync(ct);
        var run = new SyncRun
        {
            PhoneId = phone.Id,
            StartedAt = DateTime.UtcNow,
            Status = "Running",
            Action = "Probe",
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            using var client = new FanvilHttpClient(phone.IpAddress, phone.Scheme == "https");
            var (ok, msg) = await client.LoginAsync(phone.Username, phone.Password, ct);
            if (ok)
                await client.LogoutAsync(ct); // free the session slot

            var resultMsg = ok ? "Reachable, login OK" : msg;
            run.FinishedAt = DateTime.UtcNow;
            run.Status = ok ? "Success" : "Failed";
            run.HttpStatusCode = ok ? 200 : 401;
            run.Message = resultMsg;
            await db.SaveChangesAsync(ct);

            return (ok, ok ? 200 : 401, resultMsg);
        }
        catch (Exception ex)
        {
            run.FinishedAt = DateTime.UtcNow;
            run.Status = "Error";
            run.Message = ex.Message;
            try { await db.SaveChangesAsync(ct); } catch { /* best effort */ }
            return (false, null, ex.Message);
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
