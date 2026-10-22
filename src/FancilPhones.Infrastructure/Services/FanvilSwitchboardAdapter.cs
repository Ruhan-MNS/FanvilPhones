using System.Net;
using System.Text.RegularExpressions;
using FancilPhones.Application;

namespace FancilPhones.Infrastructure.Services;

/// <summary>The sideKey.htm protocol observed on the five X7C switchboards. Unknown layouts fail closed.</summary>
public sealed class FanvilSwitchboardAdapter
{
    private const string Path = "/sideKey.htm";
    private static MatchCollection Matches(string text, string pattern) => Regex.Matches(text, pattern,
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static Dictionary<string, string> Attributes(string tag) => Matches(tag, "([\\w-]+)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s>]+))")
        .Cast<Match>().GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => WebUtility.HtmlDecode(g.First().Groups[2].Success ? g.First().Groups[2].Value
            : g.First().Groups[3].Success ? g.First().Groups[3].Value : g.First().Groups[4].Value), StringComparer.OrdinalIgnoreCase);

    public static SwitchboardLayout ParsePage(string html, int page)
    {
        if (html.Length > 4 * 1024 * 1024 || page is < 1 or > 5) throw new InvalidDataException("Unsupported side-key response.");
        if (html.Contains("logonButton", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The handset returned a login page instead of its side keys. Check the stored credentials and try again.");
        if (!Matches(html, @"var\s+pageNum\s*=\s*[""']5[""']").Any()
            || !Matches(html, @"var\s+dsskeyNums\s*=\s*[""']12[""']").Any())
            throw new SwitchboardCompatibilityException("Switchboard keys requires five pages of twelve side keys. This phone did not expose that layout. Standard two-key phones can still receive phonebook updates through Phone fleet or Synchronization.");
        var forms = Matches(html, @"<form\b[^>]*>.*?</form>").Cast<Match>();
        var form = forms.SingleOrDefault(m => Attributes(m.Value[..(m.Value.IndexOf('>') + 1)]).GetValueOrDefault("id") == "SideKeyForm")
            ?? throw new InvalidDataException("The side-key form was not returned. Check handset authentication and firmware.");
        var fields = Enumerable.Range(1, 12).ToDictionary(i => i, _ => new Dictionary<string, string>(StringComparer.Ordinal));
        foreach (Match input in Matches(form.Value, @"<input\b[^>]*>"))
        {
            var attributes = Attributes(input.Value);
            var name = attributes.GetValueOrDefault("name", "");
            var match = Regex.Match(name, @"^SIDE_Key_(\w+)_RW\((\d+)\)$", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (match.Success)
            {
                var slot = int.Parse(match.Groups[2].Value);
                if (!fields.ContainsKey(slot)) throw new InvalidDataException("Unexpected key position.");
                if (match.Groups[1].Value is not ("Value1" or "Value2"))
                    fields[slot].Add(match.Groups[1].Value, attributes.GetValueOrDefault("value", ""));
            }
            match = Regex.Match(attributes.GetValueOrDefault("id", ""), @"^FUN_Key_Line_Hide\((\d+)\)$", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (match.Success) fields[int.Parse(match.Groups[1].Value)]["Line"] = attributes.GetValueOrDefault("value", "");
        }
        foreach (var row in fields.Values)
            foreach (var required in new[] { "Type", "Title", "Value", "MemorySubType", "Line", "MediaType", "PICKUP", "IconType" })
                if (!row.ContainsKey(required)) throw new InvalidDataException($"Missing required side-key field: {required}.");

        var selector = Matches(html, @"<select\b[^>]*>.*?</select>").Cast<Match>()
            .Single(m => Attributes(m.Value[..(m.Value.IndexOf('>') + 1)]).GetValueOrDefault("name") == "SIDEKEY_CONSOLE_ENTRY").Value;
        var selectedPage = SelectedValue(selector);
        if (selectedPage != page.ToString()) throw new InvalidDataException("The handset returned a different key page than requested.");
        var settings = new Dictionary<string, string>();
        foreach (Match select in Matches(html, @"<select\b[^>]*>.*?</select>"))
        {
            var attributes = Attributes(select.Value[..(select.Value.IndexOf('>') + 1)]);
            var name = attributes.GetValueOrDefault("name", "");
            if (name is "CSE_BXferWithDSSKey_RW" or "DSS_Key_Home_Page_RW" or "CSE_DssKeyLongPressAction_RW" or "CSE_SidekeyLablelength_RW")
                settings[name] = SelectedValue(select.Value);
        }
        return new(fields.Select(f => new SwitchboardKey(page, f.Key, f.Value)).ToList(), settings);
    }

    private static string SelectedValue(string select)
    {
        var options = Matches(select, @"<option\b[^>]*>").Cast<Match>().ToList();
        var selected = options.SingleOrDefault(o => Regex.IsMatch(o.Value, @"\bselected\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1))) ?? options.FirstOrDefault();
        return selected is null ? "" : Attributes(selected.Value).GetValueOrDefault("value", "");
    }

    public async Task<SwitchboardLayout> ReadAsync(IFanvilClient client, CancellationToken ct)
    {
        var keys = new List<SwitchboardKey>();
        Dictionary<string, string>? settings = null;
        for (var page = 1; page <= 5; page++)
        {
            var layout = await LoadPageAsync(client, page, ct);
            keys.AddRange(layout.Keys);
            settings ??= layout.Settings;
            if (!settings.OrderBy(x => x.Key).SequenceEqual(layout.Settings.OrderBy(x => x.Key)))
                throw new InvalidOperationException("Handset settings changed during capture. Try again.");
        }
        await LoadPageAsync(client, 1, ct);
        return new(keys, settings!);
    }

    private static async Task<SwitchboardLayout> LoadPageAsync(IFanvilClient client, int page, CancellationToken ct)
    {
        // DefaultLoad changes the selected page for this session, never the saved key configuration.
        var response = await client.PostFormAsync(Path, new Dictionary<string, string>
        { ["ReturnPage"] = Path, ["SIDEKEY_CONSOLE_ENTRY"] = page.ToString(), ["DefaultLoad"] = "Load" }, ct);
        EnsureSuccess(response);
        return ParsePage(response.Body, page);
    }

    public static Dictionary<string, string> BuildFields(IEnumerable<SwitchboardKey> keys)
    {
        var form = new Dictionary<string, string> { ["ReturnPage"] = Path, ["DefaultSubmit"] = "Apply" };
        foreach (var key in keys)
        {
            foreach (var field in key.Fields) form[$"SIDE_Key_{field.Key}_RW({key.Slot})"] = field.Value;
            form[$"SIDE_Key_Value1_RW({key.Slot})"] = key.Extension;
            form[$"SIDE_Key_Value2_RW({key.Slot})"] = key.Value2;
            form[$"sideMemoryKeySubtype({key.Slot})"] = key.Get("MemorySubType");
        }
        return form;
    }

    public async Task WriteAsync(IFanvilClient client, SwitchboardLayout before, SwitchboardLayout target, CancellationToken ct)
    {
        for (var page = 1; page <= 5; page++)
        {
            var oldPage = new SwitchboardLayout(before.Keys.Where(k => k.Page == page).ToList(), before.Settings);
            var newPage = new SwitchboardLayout(target.Keys.Where(k => k.Page == page).ToList(), target.Settings);
            var current = await LoadPageAsync(client, page, ct);
            if (current.Fingerprint() != oldPage.Fingerprint())
                throw new InvalidOperationException($"Page {page} changed since preview. Deployment stopped; review the handset before retrying.");
            if (newPage.Fingerprint() == oldPage.Fingerprint()) continue;
            EnsureSuccess(await client.PostFormAsync(Path, BuildFields(newPage.Keys), ct));
            if ((await LoadPageAsync(client, page, ct)).Fingerprint() != newPage.Fingerprint())
                throw new InvalidOperationException($"Page {page} could not be verified. Deployment stopped; earlier pages may have changed.");
        }
        if ((await ReadAsync(client, ct)).Fingerprint() != target.Fingerprint())
            throw new InvalidOperationException("Final handset verification failed. Review the saved backup and handset before retrying.");
    }

    private static void EnsureSuccess(FanvilResponse response)
    {
        if (response.StatusCode is < 200 or >= 300 || response.Body.Contains("logonButton", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The handset rejected the function-key request.");
    }
}
