using System.Text;
using FancilPhones.Data;

namespace FancilPhones.Services;

/// <summary>
/// Builds and parses the CSV phonebook format Fanvil phones import/export:
/// header "name","work","mobile","other","ring","groups" followed by quoted rows.
/// </summary>
public static class PhonebookCsv
{
    private const string Header = "\"name\",\"work\",\"mobile\",\"other\",\"ring\",\"groups\"";

    public static byte[] Build(IEnumerable<Contact> contacts)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var c in contacts)
        {
            sb.Append(Q(c.DisplayName)).Append(',')
              .Append(Q(c.OfficeNumber)).Append(',')
              .Append(Q(c.MobileNumber)).Append(',')
              .Append(Q(c.OtherNumber)).Append(',')
              .Append(Q(string.IsNullOrWhiteSpace(c.Ring) ? "Default" : c.Ring)).Append(',')
              .Append(Q(c.Group)).Append("\r\n");
        }
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    /// <summary>Parses CSV text into Contact records. Tolerates header in any column order.</summary>
    public static List<Contact> Parse(string text)
    {
        var rows = ParseRows(text);
        var result = new List<Contact>();
        if (rows.Count == 0) return result;

        // Map columns by header name; fall back to fixed positions if no header.
        int iName = 0, iWork = 1, iMobile = 2, iOther = 3, iRing = 4, iGroup = 5;
        var start = 0;
        var first = rows[0];
        if (first.Any(f => f.Trim().Equals("name", StringComparison.OrdinalIgnoreCase)))
        {
            for (var j = 0; j < first.Length; j++)
            {
                switch (first[j].Trim().ToLowerInvariant())
                {
                    case "name": iName = j; break;
                    case "work": iWork = j; break;
                    case "mobile": iMobile = j; break;
                    case "other": iOther = j; break;
                    case "ring": iRing = j; break;
                    case "groups": case "group": iGroup = j; break;
                }
            }
            start = 1;
        }

        for (var r = start; r < rows.Count; r++)
        {
            var row = rows[r];
            var name = Get(row, iName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            result.Add(new Contact
            {
                DisplayName = name.Trim(),
                OfficeNumber = NullIfEmpty(Get(row, iWork)),
                MobileNumber = NullIfEmpty(Get(row, iMobile)),
                OtherNumber = NullIfEmpty(Get(row, iOther)),
                Ring = NullIfEmpty(Get(row, iRing)) ?? "Default",
                Group = NullIfEmpty(Get(row, iGroup)),
                UpdatedAt = DateTime.UtcNow,
            });
        }
        return result;
    }

    private static string Get(string[] row, int i) => i >= 0 && i < row.Length ? row[i] : "";
    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Q(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private static List<string[]> ParseRows(string text)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                field.Append(ch); i++; continue;
            }
            switch (ch)
            {
                case '"': inQuotes = true; i++; continue;
                case ',': record.Add(field.ToString()); field.Clear(); i++; continue;
                case '\r': i++; continue;
                case '\n':
                    record.Add(field.ToString()); field.Clear();
                    rows.Add(record.ToArray()); record = new List<string>();
                    i++; continue;
                default: field.Append(ch); i++; continue;
            }
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            rows.Add(record.ToArray());
        }
        return rows;
    }
}
