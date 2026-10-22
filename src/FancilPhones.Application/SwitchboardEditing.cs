using System.Globalization;
using System.Text;

namespace FancilPhones.Application;

public static class SwitchboardEditing
{
    public static SwitchboardLayout Clone(SwitchboardLayout layout) => SwitchboardLayout.FromJson(layout.ToJson());
    public static bool SameKey(SwitchboardKey a, SwitchboardKey b) => a.Fields.OrderBy(f => f.Key, StringComparer.Ordinal)
        .SequenceEqual(b.Fields.OrderBy(f => f.Key, StringComparer.Ordinal));

    public static void ValidateDraft(string ip, SwitchboardLayout baseline, SwitchboardLayout draft)
    {
        SwitchboardRules.ValidateGeometry(ip, baseline);
        SwitchboardRules.ValidateGeometry(ip, draft);
        var first = draft.Keys.Single(k => k.Page == 1 && k.Slot == 1);
        if (first.Get("Type") != "2" || !int.TryParse(first.Get("Line"), out var line) || line < 1)
            throw new SwitchboardValidationException("Page 1 F1 must remain a configured Line key. Correct the line on the phone and read its keys again.");
        if (!baseline.Settings.OrderBy(f => f.Key).SequenceEqual(draft.Settings.OrderBy(f => f.Key)))
            throw new SwitchboardValidationException("The key editor cannot change handset-wide settings.");
        foreach (var key in draft.Keys)
        {
            var old = baseline.Keys.Single(k => k.Page == key.Page && k.Slot == key.Slot);
            if (key.Protected)
            {
                if (!SameKey(old, key)) throw new SwitchboardValidationException("Page 1 F1 and F2 are protected and must stay exactly as captured.");
                continue;
            }
            if (!key.Fields.Keys.Order().SequenceEqual(old.Fields.Keys.Order()))
                throw new SwitchboardValidationException("The layout contains fields that do not match this phone's firmware.");
            if (SameKey(old, key)) continue; // Unrecognized, unchanged key types remain intact.
            if (key.Get("Type") is not ("0" or "1" or "2"))
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: choose None, Memory Key or Line. Other key types can only be retained unchanged.");
            if (key.Get("Line") != first.Get("Line"))
                throw new SwitchboardValidationException("Edited keys must use this phone's own SIP line.");
            if (key.Fields.Values.Any(v => v is null || v.Any(char.IsControl)))
                throw new SwitchboardValidationException("Key fields cannot contain control characters.");
            if (Encoding.UTF8.GetByteCount(key.Name) > 63 || Encoding.UTF8.GetByteCount(key.Get("Value")) > 31
                || Encoding.UTF8.GetByteCount(key.Get("PICKUP")) > 31 || key.Get("Value").Count(c => c == '|') > 1)
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: name allows 63 UTF-8 bytes; Value and Value2 together, and pickup number, each allow 31 bytes.");
            if (key.Get("Type") == "1" && (string.IsNullOrWhiteSpace(key.Name) || string.IsNullOrWhiteSpace(key.Extension)))
                throw new SwitchboardValidationException($"Page {key.Page} F{key.Slot}: a Memory Key needs a name and a value.");
        }
        if (draft.Keys.Where(k => !k.Protected && k.Get("Type") == "1").GroupBy(k => SwitchboardRules.NameKey(k.Name)).Any(g => g.Count() > 1))
            throw new SwitchboardValidationException("Give each Memory Key a unique name so phone-specific numbers can be matched reliably.");
    }

    public static SwitchboardLayout Sort(SwitchboardLayout draft)
    {
        var result = Clone(draft);
        var positions = result.Keys.Where(k => !k.Protected && k.Get("Type") is "0" or "1").OrderBy(k => k.Page).ThenBy(k => k.Slot).ToList();
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

    public static SwitchboardLayout Copy(SwitchboardLayout source, SwitchboardLayout target, bool keepTargetNumbers)
    {
        var result = Clone(target);
        var numbers = target.Keys.Where(k => !k.Protected && k.Get("Type") == "1" && k.Name.Length > 0)
            .GroupBy(k => SwitchboardRules.NameKey(k.Name)).ToDictionary(g => g.Key, g => g.ToList());
        var line = target.Keys.Single(k => k.Page == 1 && k.Slot == 1).Get("Line");
        foreach (var position in result.Keys.Where(k => !k.Protected))
        {
            var key = source.Keys.Single(k => k.Page == position.Page && k.Slot == position.Slot);
            var fields = new Dictionary<string, string>(key.Fields);
            fields["Line"] = line;
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
