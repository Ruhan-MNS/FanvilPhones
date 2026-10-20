using System.Text.RegularExpressions;
using FancilPhones.Domain;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace FancilPhones.Infrastructure.Services;

public sealed class PhoneSyncService(
    IDbContextFactory<AppDbContext> dbf,
    ILogger<PhoneSyncService> logger,
    IUserAccess access,
    IFanvilClientFactory clients,
    IOptions<PhoneOperationOptions> options,
    IHostApplicationLifetime application) : IPhoneSyncService
{
    private PhoneOperationOptions Settings => options.Value;
    private sealed record HandsetResult(bool Success, int? HttpStatus, string Message);

    public async Task<IReadOnlyList<SyncResult>> SyncAllAsync(CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        List<Phone> phones;
        List<Contact> contacts;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            phones = await db.Phones.AsNoTracking().Where(p => p.Enabled).OrderBy(p => p.Id).ToListAsync(ct);
            contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct);
        }
        var csv = PhonebookCsv.Build(contacts, ct);
        using var scope = logger.BeginScope("FleetSyncId:{FleetSyncId}", Guid.NewGuid());
        logger.LogInformation("Fleet synchronization started for {PhoneCount} phones with {ContactCount} contacts.", phones.Count, contacts.Count);
        var results = new SyncResult[phones.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, phones.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = Settings.MaxConcurrentSyncs,
            CancellationToken = ct
        }, async (index, token) => results[index] = await PushAsync(phones[index], contacts.Count, csv, token));
        logger.LogInformation("Fleet synchronization completed. Successful: {SuccessCount}; failed: {FailureCount}.",
            results.Count(r => r.Success), results.Count(r => !r.Success));
        return results;
    }

    public async Task<SyncResult> SyncOneAsync(int phoneId, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == phoneId, ct);
        if (phone is null) return new(phoneId, "?", false, null, "Phone not found");
        var contacts = await db.Contacts.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync(ct);
        return await PushAsync(phone, contacts.Count, PhonebookCsv.Build(contacts, ct), ct);
    }

    private async Task<SyncResult> PushAsync(Phone phone, int count, byte[] csv, CancellationToken ct)
    {
        var result = await RunHandsetAsync(phone, "Sync", count, async (client, token) =>
        {
            var deleted = await client.DeleteAllContactsAsync(token);
            if (deleted.StatusCode is < 200 or >= 400)
                return new(false, deleted.StatusCode, "The handset rejected clearing its phonebook; upload was not attempted.");
            var response = await client.PostFileAsync(phone.UploadPath, phone.UploadFieldName, "phonebook.csv", csv, "application/octet-stream", token);
            var ok = response.StatusCode is >= 200 and < 400;
            return new(ok, response.StatusCode, ok ? $"Uploaded {count} contacts (HTTP {response.StatusCode})" : $"HTTP {response.StatusCode}: {Truncate(response.Body, 400)}");
        }, ct);
        return new(phone.Id, phone.Name, result.Success, result.HttpStatus, result.Message);
    }

    public async Task<(bool ok, string message)> PushSipDisplayNameAsync(int phoneId, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAdministratorAsync(ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var phone = await db.Phones.AsNoTracking().SingleOrDefaultAsync(p => p.Id == phoneId, ct);
        if (phone is null) return (false, "Phone not found");
        if (string.IsNullOrWhiteSpace(phone.SipDisplayName) && string.IsNullOrWhiteSpace(phone.SipExtension))
            return (false, "No SIP display name or extension configured for this phone.");
        var result = await RunHandsetAsync(phone, "Push", 0, (client, token) => PushSipSettingsAsync(client, phone, token), ct);
        return (result.Success, result.Message);
    }

    public async Task<(bool reachable, int? status, string message)> ProbeAsync(Phone phone, CancellationToken ct = default)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, application.ApplicationStopping);
        ct = stopping.Token;
        await access.RequireAuthenticatedAsync(ct);
        var result = await RunHandsetAsync(phone, "Probe", 0,
            (_, _) => Task.FromResult(new HandsetResult(true, 200, "Reachable, login OK")), ct);
        return (result.Success, result.HttpStatus, result.Message);
    }

    // Each operation owns its timeout and client. Cleanup gets a separate, short token
    // so caller cancellation cannot strand sessions or leave history marked Running.
    private async Task<HandsetResult> RunHandsetAsync(Phone phone, string action, int contactCount,
        Func<IFanvilClient, CancellationToken, Task<HandsetResult>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = new SyncRun { PhoneId = phone.Id, Action = action, Status = "Running", ContactCount = contactCount };
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            db.SyncRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }
        using var scope = logger.BeginScope("PhoneId:{PhoneId} SyncRunId:{SyncRunId} Operation:{Operation}", phone.Id, run.Id, action);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Settings.TimeoutSeconds));
        IFanvilClient? client = null;
        HandsetResult result;
        string status;
        bool canceled = false;
        logger.LogInformation("Handset operation started.");
        try
        {
            client = clients.Create(phone.IpAddress, phone.Scheme == "https");
            var login = await client.LoginAsync(phone.Username, phone.Password, timeout.Token);
            result = login.ok ? await operation(client, timeout.Token) : new(false, 401, login.message);
            timeout.Token.ThrowIfCancellationRequested();
            status = result.Success ? "Success" : "Failed";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            canceled = true;
            status = "Cancelled";
            result = new(false, null, "Operation cancelled. The handset may have applied partial changes; verify its state before retrying.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            status = "TimedOut";
            result = new(false, null, "The handset operation timed out. Verify its state before retrying.");
        }
        catch (TimeoutException)
        {
            status = "TimedOut";
            result = new(false, null, "Timed out waiting for the handset response. Verify its state before retrying.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Handset operation failed.");
            status = "Error";
            result = new(false, null, ex.Message);
        }
        finally
        {
            if (client is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.CleanupTimeoutSeconds));
                try { await client.LogoutAsync(cleanup.Token); }
                catch (Exception ex) { logger.LogDebug(ex, "Best-effort handset logout did not complete."); }
                finally { client.Dispose(); }
            }
        }

        using var persist = new CancellationTokenSource(TimeSpan.FromSeconds(Settings.CleanupTimeoutSeconds));
        try { await FinishRunAsync(run.Id, phone.Id, status, result, action != "Probe", persist.Token); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not persist the handset operation result {Status}.", status);
            if (!canceled) throw;
        }
        logger.Log(canceled || result.Success ? LogLevel.Information : LogLevel.Warning,
            "Handset operation finished with {Status}; HTTP {HttpStatus}.", status, result.HttpStatus);
        if (canceled) throw new OperationCanceledException(ct);
        return result;
    }

    private async Task FinishRunAsync(int runId, int phoneId, string status, HandsetResult result, bool updatePhone, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var finished = DateTime.UtcNow;
        await db.SyncRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(setters => setters
            .SetProperty(r => r.FinishedAt, finished).SetProperty(r => r.Status, status)
            .SetProperty(r => r.HttpStatusCode, result.HttpStatus).SetProperty(r => r.Message, result.Message), ct);
        if (updatePhone)
            await db.Phones.Where(p => p.Id == phoneId).ExecuteUpdateAsync(setters => setters
                .SetProperty(p => p.LastSyncedAt, finished).SetProperty(p => p.LastSyncStatus, status)
                .SetProperty(p => p.LastSyncMessage, result.Message), ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<HandsetResult> PushSipSettingsAsync(IFanvilClient client, Phone phone, CancellationToken ct)
    {
        var displayName = phone.SipDisplayName?.Trim();
        var extension = phone.SipExtension?.Trim();
        var lineIdx = 1;
        var lineNote = phone.SipLineIndex > 1 ? $" (requested line {phone.SipLineIndex}; multi-line not yet supported, pushed to line 1)" : "";
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
        {
            logger.LogWarning("SIP settings push stopped: the handset response did not contain the expected form.");
            return new(false, null, "Could not locate the SIP form on the phone " +
                           "(no <form> contains SIP_DisPlayName_R). Aborted.");
        }

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

        logger.LogDebug("Submitting {FieldCount} SIP configuration fields for line {LineIndex}.", post.Count, lineIdx);

        var resp = await client.PostFormAsync("/lines.htm", post, ct);
        var body = resp.Body ?? "";
        logger.LogDebug("SIP configuration response: HTTP {HttpStatus}, {ResponseLength} characters.",
            resp.StatusCode, body.Length);

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
            return new(false, resp.StatusCode, fail);
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

        return new(ok, resp.StatusCode, msg);
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

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
