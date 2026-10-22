namespace FancilPhones.Application;

public sealed record KeyOption(string Value, string Text);

// Values and field names verified against the X7C side-key form. No configuration is posted during discovery.
public static class SwitchboardKeyOptions
{
    public static IReadOnlyList<KeyOption> Types { get; } = [new("0", "None"), new("1", "Memory Key"), new("2", "Line"), new("3", "Key Event"), new("4", "DTMF"), new("7", "URL"), new("13", "BLF List Key"), new("14", "MCAST Paging"), new("20", "Action URL"), new("21", "XML Browser"), new("22", "MCAST Listening"), new("24", "PTT")];
    public static IReadOnlyList<KeyOption> MemorySubtypes { get; } = [new("21", "None"), new("90", "BLF/NEW CALL"), new("91", "BLF/BXFER"), new("92", "BLF/AXFER"), new("93", "BLF/CONF"), new("94", "BLF/DTMF"), new("23", "Presence"), new("24", "Voice Mail"), new("25", "Speed Dial"), new("26", "Intercom"), new("27", "Call Park"), new("28", "Call forward")];
    public static IReadOnlyList<KeyOption> Events { get; } = [new("20", "None"), new("0", "Voice Mail"), new("1", "Do-not-disturb"), new("2", "Call hold"), new("3", "Call transfer"), new("5", "Phonebook"), new("6", "Redial"), new("7", "Pickup"), new("8", "Join"), new("11", "Call forward"), new("12", "Call Logs"), new("13", "Flash"), new("14", "Memo"), new("15", "Headset"), new("16", "Release"), new("17", "Lock phone"), new("18", "SMS"), new("19", "Call Back"), new("21", "Hide DTMF"), new("23", "Intercom"), new("24", "Group Listening"), new("30", "Prefix"), new("50", "Hot Desking"), new("51", "Agent"), new("52", "End"), new("53", "Disposition"), new("54", "Escalate"), new("55", "Trace"), new("56", "Handfree"), new("57", "Answer Key"), new("58", "Private Hold"), new("59", "Local Contacts"), new("60", "LDAP Group"), new("61", "XML Group"), new("62", "Broadsoft Group"), new("63", "Record"), new("64", "Auto Headset"), new("65", "LongPress Mute Key")];
    public static IReadOnlyList<KeyOption> Codecs { get; } = [new("PCMU", "G.711U"), new("PCMA", "G.711A"), new("G726-16", "G.726-16"), new("G726-24", "G.726-24"), new("G726-32", "G.726-32"), new("G726-40", "G.726-40"), new("G729", "G.729AB"), new("G723", "G.723.1"), new("iLBC", "iLBC"), new("AMR", "AMR"), new("opus", "opus"), new("G722", "G.722"), new("AMR-WB", "AMR-WB")];
    public static IReadOnlyList<KeyOption> HttpMethods { get; } = [new("0", "HTTP(S) GET"), new("1", "HTTP(S) POST")];
    public static IReadOnlyList<KeyOption> PttModes { get; } = [new("25", "Speed Dial"), new("26", "Intercom"), new("14", "Multicast")];
    public static IReadOnlyList<KeyOption> Media { get; } = [new("0", "Default"), new("1", "Audio"), new("2", "Video")];
    public static IReadOnlyList<KeyOption> Icons { get; } = [new("Green", "Default Green"), new("Blue", "Default Blue"), new("Yellow", "Default Yellow"), new("Red", "Default Red"), new("Purple", "Default Purple"), new("Custom", "Custom (configured on phone)")];
    public static bool HasValue(SwitchboardKey k) => k.Get("Type") is "1" or "4" or "7" or "14" or "20" or "21" or "22" or "24" || k.Get("Type") == "3" && k.Get("KeyeventSubType") is "30" or "53" or "54";
    public static bool HasLine(SwitchboardKey k) => k.Get("Type") is "1" or "2" || k.Get("Type") == "24" && k.Get("PttSubType") != "14";
    public static int ValueLimit(SwitchboardKey k) => k.Get("Type") is "7" or "20" or "21" ? 127 : 31;
    public static int CombinedValueLimit(SwitchboardKey k) => k.Get("Type") is "7" or "20" or "21" ? 255 : 31;
    public static string TypeLabel(string value) => Label(Types, value);
    public static string Label(IEnumerable<KeyOption> options, string value) => options.FirstOrDefault(o => o.Value == value)?.Text ?? (value.Length == 0 ? "None" : $"Captured ({value})");
    public static string SubtypeField(SwitchboardKey k) => k.Get("Type") switch { "1" => "MemorySubType", "3" => "KeyeventSubType", "14" => "CodecSubType", "20" => "ActionURLSubType", "24" => "PttSubType", _ => "" };
    public static IReadOnlyList<KeyOption> Subtypes(SwitchboardKey k) => k.Get("Type") switch { "1" => MemorySubtypes, "3" => Events, "14" => Codecs, "20" => HttpMethods, "24" => PttModes, _ => [] };
    public static string DetailField(SwitchboardKey k) => k.Get("Type") == "24" && k.Get("PttSubType") == "14" ? "PttSubTypeValue" : k.Get("Type") == "3" ? k.Get("KeyeventSubType") switch { "60" => "LdapSubType", "61" => "XmlSubType", "62" => "BWSubType", _ => "" } : "";
    public static IReadOnlyList<KeyOption> Details(SwitchboardKey k) => DetailField(k) switch
    {
        "PttSubTypeValue" => Codecs,
        "LdapSubType" => [new("0", "Auto"), new("1", "LDAP1"), new("2", "LDAP2"), new("3", "LDAP3"), new("4", "LDAP4")],
        "XmlSubType" => [new("0", "Auto"), new("1", "Contacts"), new("2", "XML2"), new("3", "XML3"), new("4", "XML4")],
        "BWSubType" => [new("0", "Auto"), new("1", "Broadsoft1"), new("2", "Broadsoft2"), new("3", "Broadsoft3"), new("4", "Broadsoft4"), new("5", "Broadsoft5"), new("6", "Broadsoft6")], _ => []
    };
    public static string SubtypeLabel(SwitchboardKey k)
    {
        var field = SubtypeField(k); if (field.Length == 0) return "None";
        var text = Label(Subtypes(k), k.Get(field)); var detail = DetailField(k);
        return detail.Length == 0 ? text : text + " · " + Label(Details(k), k.Get(detail));
    }
    public static void ChangeType(SwitchboardKey key, string type, string line)
    {
        if (key.Get("Type") == type) return;
        key.Fields["Type"] = type; key.Fields["Line"] = line; key.Fields["Value"] = ""; key.Fields["PICKUP"] = ""; key.Fields["MediaType"] = "0";
        foreach (var field in new[] { "MemorySubType", "KeyeventSubType", "CodecSubType", "PttSubType", "PttSubTypeValue", "LdapSubType", "XmlSubType", "BWSubType" })
            if (key.Fields.ContainsKey(field)) key.Fields[field] = "";
        if (type == "0") key.Fields["Title"] = "";
        var subtype = SubtypeField(key);
        if (subtype.Length > 0) key.Fields[subtype] = type switch { "1" => "91", "3" => "20", "14" => "PCMU", "20" => "0", "24" => "25", _ => "" };
    }
    public static void ChangeSubtype(SwitchboardKey key, string value)
    {
        var field = SubtypeField(key); if (field.Length == 0) return;
        key.Fields[field] = value;
        var detail = DetailField(key); if (detail.Length > 0 && string.IsNullOrEmpty(key.Get(detail))) key.Fields[detail] = detail == "PttSubTypeValue" ? "PCMU" : "0";
    }
}
