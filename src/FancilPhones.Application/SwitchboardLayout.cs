using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FancilPhones.Domain;

namespace FancilPhones.Application;

public sealed class SwitchboardValidationException(string message) : InvalidOperationException(message);

public sealed record SwitchboardKey(int Page, int Slot, Dictionary<string, string> Fields)
{
    public string Get(string field) => Fields.GetValueOrDefault(field, "");
    public string Name => Get("Title");
    public string Extension => Get("Value").Split('|', 2)[0];
    public string Value2 => Get("Value").Split('|', 2).ElementAtOrDefault(1) ?? "";
    public string TypeLabel => SwitchboardKeyOptions.TypeLabel(Get("Type"));
}

public sealed record SwitchboardLayout(List<SwitchboardKey> Keys, Dictionary<string, string> Settings)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static SwitchboardLayout FromJson(string json) => JsonSerializer.Deserialize<SwitchboardLayout>(json)
        ?? throw new InvalidOperationException("The saved layout is invalid.");
    public string Fingerprint() => SwitchboardRules.Hash(JsonSerializer.Serialize(new
    {
        Keys = Keys.OrderBy(k => k.Page).ThenBy(k => k.Slot).Select(k => new { k.Page, k.Slot, Fields = k.Fields.OrderBy(f => f.Key, StringComparer.Ordinal) }),
        Settings = Settings.OrderBy(f => f.Key, StringComparer.Ordinal)
    }));
}

public static class SwitchboardRules
{
    // Only the retained legacy shared-directory generator reserves these positions.
    // The current per-phone editor and sync workflow can edit every position.
    public static bool LegacyDirectoryReserved(SwitchboardKey key) => key.Page == 1 && key.Slot <= 2;
    public const string EmptyDirectoryMessage = "Import contacts from a captured phone, or select Include beside at least one contact in the Master key directory before previewing.";
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string NameKey(string name) => Regex.Replace(name.Trim(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).ToUpperInvariant();

    public static void ValidateGeometry(string ip, SwitchboardLayout layout)
    {
        if (layout.Keys.Count != 60 || layout.Keys.Select(k => (k.Page, k.Slot)).Distinct().Count() != 60
            || layout.Keys.Any(k => k.Page is < 1 or > 5 || k.Slot is < 1 or > 12))
            throw new InvalidOperationException("A verified five-page, twelve-key layout is required.");
    }

    public static void Validate(string ip, SwitchboardLayout layout)
    {
        ValidateGeometry(ip, layout);
        if (layout.Keys.Any(k => !LegacyDirectoryReserved(k) && k.Get("Type") is not ("0" or "1")))
            throw new InvalidOperationException("An editable position contains a special key. It must be reviewed before automatic ordering.");
        var duplicate = layout.Keys.Where(k => !LegacyDirectoryReserved(k) && k.Name.Length > 0).GroupBy(k => NameKey(k.Name)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException("Duplicate contact names on this handset must be reconciled before ordering.");
    }

    public static SwitchboardLayout Generate(string ip, SwitchboardLayout baseline, IReadOnlyList<SwitchboardMember> members,
        IReadOnlyList<SwitchboardOverride> overrides)
    {
        Validate(ip, baseline);
        if (members.Count == 0) throw new SwitchboardValidationException(EmptyDirectoryMessage);
        if (members.Count > 58) throw new InvalidOperationException("The shared-directory layout has capacity for 58 contacts after its two existing header keys.");
        if (members.Select(m => NameKey(m.Contact.DisplayName)).Distinct().Count() != members.Count)
            throw new InvalidOperationException("Switchboard contact names must be unique.");
        var line = SwitchboardEditing.PhoneLine(baseline);
        var existing = baseline.Keys.Where(k => !LegacyDirectoryReserved(k) && k.Name.Length > 0).ToDictionary(k => NameKey(k.Name));
        var ordered = members.OrderBy(m => string.IsNullOrWhiteSpace(m.SortName) ? m.Contact.DisplayName : m.SortName,
            StringComparer.Create(CultureInfo.GetCultureInfo("en-ZA"), true)).ThenBy(m => m.ContactId).ToList();
        var result = new List<SwitchboardKey>();
        var empty = baseline.Keys.FirstOrDefault(k => !LegacyDirectoryReserved(k) && k.Get("Type") == "0");
        var standard = baseline.Keys.FirstOrDefault(k => !LegacyDirectoryReserved(k) && k.Get("Type") == "1" && k.Get("MemorySubType") is "91" or "92");
        var index = 0;
        foreach (var position in baseline.Keys.OrderBy(k => k.Page).ThenBy(k => k.Slot))
        {
            if (LegacyDirectoryReserved(position)) { result.Add(position with { Fields = new(position.Fields) }); continue; }
            var fields = empty is null ? position.Fields.ToDictionary(f => f.Key, f => f.Key == "ActionURLSubType" ? "0" : "") : new(empty.Fields);
            fields["Type"] = "0"; fields["Line"] = line; fields["IconType"] = "Green";
            if (index < ordered.Count)
            {
                var member = ordered[index++];
                existing.TryGetValue(NameKey(member.Contact.DisplayName), out var old);
                var special = overrides.SingleOrDefault(o => o.ContactId == member.ContactId);
                if (special?.CapturedKeyJson is not null)
                    old = JsonSerializer.Deserialize<SwitchboardKey>(special.CapturedKeyJson) ?? throw new InvalidOperationException("Invalid captured contact settings.");
                var extension = special?.Extension ?? member.Contact.OfficeNumber ?? "";
                var secondary = special?.Value2 ?? old?.Value2 ?? "";
                if (old is not null) fields = new(old.Fields);
                else if (standard is not null) { fields = new(standard.Fields); fields["PICKUP"] = ""; fields["IconType"] = "Green"; }
                fields["Type"] = "1"; fields["Title"] = member.Contact.DisplayName.Trim();
                fields["Value"] = extension.Trim() + (secondary.Length > 0 ? "|" + secondary.Trim() : "");
                fields["Line"] = line;
                if (old is null) { fields["MemorySubType"] = standard?.Get("MemorySubType") ?? "91"; fields["MediaType"] = "0"; }
                if (string.IsNullOrWhiteSpace(extension) || extension.Contains('|') || secondary.Contains('|')
                    || Encoding.UTF8.GetByteCount(fields["Title"]) > 63 || Encoding.UTF8.GetByteCount(fields["Value"]) > 31
                    || fields["Value"].Any(char.IsControl) || fields["Title"].Any(char.IsControl))
                    throw new InvalidOperationException($"Contact '{member.Contact.DisplayName}' has an empty or invalid number, or exceeds handset field limits.");
            }
            result.Add(new(position.Page, position.Slot, fields));
        }
        return new(result, new(baseline.Settings));
    }
}

public sealed record SwitchboardPhone(int Id, string Name, string IpAddress, DateTime? CapturedAt, string? ConfigurationWarning = null, bool HasDraft = false);
public sealed record SwitchboardFleetPhone(int Id, string Name, string IpAddress, bool Enabled);
public sealed record SwitchboardEditor(int PhoneId, Guid Revision, SwitchboardLayout Baseline, SwitchboardLayout Layout, bool SortAlphabetically = true);
public sealed record SwitchboardState(List<SwitchboardPhone> Phones, List<Contact> Contacts, List<SwitchboardMember> Members,
    List<SwitchboardOverride> Overrides, List<SwitchboardDeployment> History)
{
    public List<SwitchboardFleetPhone> FleetPhones { get; init; } = [];
}
public interface ISwitchboardService
{
    Task<SwitchboardState> GetStateAsync(CancellationToken ct = default);
    Task CaptureAsync(int phoneId, CancellationToken ct = default);
    Task RemovePhoneAsync(int phoneId, CancellationToken ct = default);
    Task<SwitchboardEditor> GetEditorAsync(int phoneId, CancellationToken ct = default);
    Task<SwitchboardEditor> SaveDraftAsync(int phoneId, Guid revision, SwitchboardLayout layout, bool sortAlphabetically = true, CancellationToken ct = default);
    Task<SwitchboardDeployment> PreviewDraftAsync(int sourcePhoneId, int targetPhoneId, bool keepTargetNumbers, CancellationToken ct = default);
    Task ImportContactsAsync(int phoneId, CancellationToken ct = default);
    Task SetMemberAsync(int contactId, bool included, string? sortName, CancellationToken ct = default);
    Task SaveOverrideAsync(int phoneId, int contactId, string? extension, string? value2, CancellationToken ct = default);
    Task<SwitchboardDeployment> PreviewAsync(int phoneId, CancellationToken ct = default);
    Task<SwitchboardDeployment> PreviewRestoreAsync(Guid deploymentId, CancellationToken ct = default);
    Task<SwitchboardDeployment> DeployAsync(Guid previewId, CancellationToken ct = default);
}
