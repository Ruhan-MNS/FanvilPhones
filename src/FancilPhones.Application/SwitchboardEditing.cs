using System.Globalization;
using System.Text;

namespace FancilPhones.Application;

public static class SwitchboardEditing
{
    public static SwitchboardLayout Clone(SwitchboardLayout layout) => SwitchboardLayout.FromJson(layout.ToJson());
    public static string PhoneLine(SwitchboardLayout layout)
    {
        // F1 can now be any type or use another SIP account; do not infer the primary line from F1 alone.
        var active = layout.Keys.Where(k => k.Get("Type") != "0" && SwitchboardKeyOptions.HasLine(k)).ToList();
        return (active.Count > 0 ? active : layout.Keys).Select(k => k.Get("Line"))
            .Where(v => int.TryParse(v, out var n) && n is >= 1 and <= 20).GroupBy(v => v)
            .OrderByDescending(g => g.Count()).ThenBy(g => int.Parse(g.Key)).FirstOrDefault()?.Key ?? "1";
    }
    public static bool SameKey(SwitchboardKey a, SwitchboardKey b) => a.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)
        .SequenceEqual(b.Fields.OrderBy(f => f.Key, StringComparer.Ordinal));

    public static void ValidateDraft(string ip, SwitchboardLayout baseline, SwitchboardLayout draft)
    {
        SwitchboardRules.ValidateGeometry(ip, baseline);
        SwitchboardRules.ValidateGeometry(ip, draft);
        if (!baseline.Settings.OrderBy(f => f.Key).SequenceEqual(draft.Settings.OrderBy(f => f.Key)))
            throw new SwitchboardValidationException("The key editor cannot change handset-wide settings.");
        foreach (var key in draft.Keys)
        {
            var old = baseline.Keys.Single(k => k.Page == key.Page && k.Slot == key.Slot);
            if (!key.Fields.Keys.Order().SequenceEqual(old.Fields.Keys.Order()))
                throw new SwitchboardValidationException("The layout contains fields that do not match this phone's firmware.");
            if (SameKey(old, key)) continue; // Unrecognized, unchanged key types remain intact.
            if (!SwitchboardKeyOptions.Types.Any(t => t.Value == key.Get("Type")))
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: unsupported key types can only be retained unchanged.");
            if (SwitchboardKeyOptions.HasLine(key) && key.Get("Line") != "-1" && key.Get("Line") != "23" && (!int.TryParse(key.Get("Line"), out var line) || line is < 1 or > 20))
                throw new SwitchboardValidationException("Choose Auto, a SIP line from 1 to 20, or Mobile.");
            if (key.Fields.Values.Any(v => v is null || v.Any(char.IsControl)))
                throw new SwitchboardValidationException("Key fields cannot contain control characters.");
            if (Encoding.UTF8.GetByteCount(key.Name) > 63 || Encoding.UTF8.GetByteCount(key.Get("Value")) > SwitchboardKeyOptions.CombinedValueLimit(key)
                || Encoding.UTF8.GetByteCount(key.Extension) > SwitchboardKeyOptions.ValueLimit(key) || Encoding.UTF8.GetByteCount(key.Value2) > SwitchboardKeyOptions.ValueLimit(key)
                || Encoding.UTF8.GetByteCount(key.Get("PICKUP")) > 31 || key.Get("Value").Count(c => c == '|') > 1)
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: name, value or pickup number exceeds the limit for this key type, or contains an extra pipe character.");
            if (key.Get("Type") == "1" && (string.IsNullOrWhiteSpace(key.Name) || string.IsNullOrWhiteSpace(key.Extension)))
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: a Memory Key needs a name and a value.");
            if (SwitchboardKeyOptions.HasValue(key) && string.IsNullOrWhiteSpace(key.Extension))
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: enter a value for {key.TypeLabel}.");
            foreach (var field in new[] { SwitchboardKeyOptions.SubtypeField(key), SwitchboardKeyOptions.DetailField(key) }.Where(f => f.Length > 0))
            {
                var options = field == SwitchboardKeyOptions.SubtypeField(key) ? SwitchboardKeyOptions.Subtypes(key) : SwitchboardKeyOptions.Details(key);
                if ((key.Get("Type") != old.Get("Type") || key.Get(field) != old.Get(field))
                    && !(field == "MemorySubType" && key.Get(field) == "") && !options.Any(o => o.Value == key.Get(field)))
                    throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: choose a valid subtype for {key.TypeLabel}.");
            }
        }
        if (draft.Keys.Where(k => k.Get("Type") == "1").GroupBy(k => SwitchboardRules.NameKey(k.Name)).Any(g => g.Count() > 1))
            throw new SwitchboardValidationException("Give each Memory Key a unique name so phone-specific numbers can be matched reliably.");
    }

    public static SwitchboardLayout Sort(SwitchboardLayout draft)
    {
        var result = Clone(draft);
        // Keep the two desk utility positions in place during A–Z, while allowing all their fields to be edited and synced.
        var positions = result.Keys.Where(k => !(k.Page == 1 && k.Slot <= 2) && k.Get("Type") is "0" or "1").OrderBy(k => k.Page).ThenBy(k => k.Slot).ToList();
        var ordered = positions.Where(k => k.Get("Type") == "1").OrderBy(k => k.Name,
            StringComparer.Create(CultureInfo.GetCultureInfo("en-ZA"), true)).Concat(positions.Where(k => k.Get("Type") == "0")).ToList();
        var fields = ordered.Select(k => new Dictionary<string, string>(k.Fields)).ToList();
        for (var i = 0; i < positions.Count; i++)
        {
            positions[i].Fields.Clear();
            foreach (var field in fields[i]) positions[i].Fields[field.Key] = field.Value;
        }
        return result;
    }

    public static SwitchboardLayout Copy(SwitchboardLayout source, SwitchboardLayout target, bool keepTargetNumbers, string? sourceLine = null)
    {
        var result = Clone(target);
        var numbers = target.Keys.Where(k => k.Get("Type") == "1" && k.Name.Length > 0)
            .GroupBy(k => SwitchboardRules.NameKey(k.Name)).ToDictionary(g => g.Key, g => g.ToList());
        var line = PhoneLine(target); sourceLine ??= PhoneLine(source);
        foreach (var position in result.Keys)
        {
            var key = source.Keys.Single(k => k.Page == position.Page && k.Slot == position.Slot);
            var fields = new Dictionary<string, string>(key.Fields);
            if (key.Get("Line") == sourceLine) fields["Line"] = line;
            if (keepTargetNumbers && key.Get("Type") == "1" && numbers.TryGetValue(SwitchboardRules.NameKey(key.Name), out var matches))
            {
                if (matches.Count != 1) throw new SwitchboardValidationException($"'{key.Name}' appears more than once on the target phone. Resolve it before keeping phone-specific numbers.");
                fields["Value"] = matches[0].Get("Value");
            }
            position.Fields.Clear();
            foreach (var field in fields) position.Fields[field.Key] = field.Value;
        }
        return result;
    }
}
