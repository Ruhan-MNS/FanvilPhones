using ClosedXML.Excel;
using FancilPhones.Data;

namespace FancilPhones.Services;

/// <summary>
/// Parses a Phones export XLSX (header row: Name, IP, Scheme, Username, Password,
/// UploadPath, UploadFieldName, Enabled). Lookups are case-insensitive and tolerate
/// missing optional columns by falling back to <see cref="Phone"/>'s defaults.
/// </summary>
public static class PhonesXlsx
{
    public static List<Phone> Parse(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault()
                 ?? throw new InvalidOperationException("Workbook has no sheets.");

        var headerRow = ws.FirstRowUsed();
        if (headerRow is null) return new List<Phone>();

        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
            cols[cell.GetString().Trim()] = cell.Address.ColumnNumber;

        int? Col(params string[] names)
        {
            foreach (var n in names)
                if (cols.TryGetValue(n, out var c)) return c;
            return null;
        }

        var nameCol = Col("Name") ?? throw new InvalidOperationException("Missing 'Name' column.");
        var ipCol = Col("IP", "IpAddress", "IP Address") ?? throw new InvalidOperationException("Missing 'IP' column.");
        var schemeCol = Col("Scheme");
        var userCol = Col("Username");
        var passCol = Col("Password");
        var pathCol = Col("UploadPath", "Upload Path");
        var fieldCol = Col("UploadFieldName", "Upload Field Name");
        var enabledCol = Col("Enabled");
        var sipNameCol = Col("SipDisplayName", "SIP Display Name");
        var sipLineCol = Col("SipLineIndex", "SIP Line", "Line");
        var sipExtCol = Col("SipExtension", "SIP Extension", "Extension");
        var sipRegCol = Col("SipRegistrationEnabled", "Registration Enabled", "Activate");

        var phones = new List<Phone>();
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var name = row.Cell(nameCol).GetString().Trim();
            var ip = row.Cell(ipCol).GetString().Trim();
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(ip)) continue;
            if (string.IsNullOrWhiteSpace(ip)) continue;

            var p = new Phone
            {
                Name = string.IsNullOrWhiteSpace(name) ? ip : name,
                IpAddress = ip,
            };
            if (schemeCol is int sc)
            {
                var v = row.Cell(sc).GetString().Trim().ToLowerInvariant();
                if (v is "http" or "https") p.Scheme = v;
            }
            if (userCol is int uc)
            {
                var v = row.Cell(uc).GetString().Trim();
                if (!string.IsNullOrEmpty(v)) p.Username = v;
            }
            if (passCol is int pc)
            {
                var v = row.Cell(pc).GetString();
                if (!string.IsNullOrEmpty(v)) p.Password = v;
            }
            if (pathCol is int pthc)
            {
                var v = row.Cell(pthc).GetString().Trim();
                if (!string.IsNullOrEmpty(v)) p.UploadPath = v;
            }
            if (fieldCol is int fc)
            {
                var v = row.Cell(fc).GetString().Trim();
                if (!string.IsNullOrEmpty(v)) p.UploadFieldName = v;
            }
            if (enabledCol is int ec)
            {
                var cell = row.Cell(ec);
                if (cell.DataType == XLDataType.Boolean)
                    p.Enabled = cell.GetBoolean();
                else
                {
                    var s = cell.GetString().Trim().ToLowerInvariant();
                    p.Enabled = s is "true" or "1" or "yes" or "y" or "enabled";
                }
            }

            if (sipNameCol is int snc)
            {
                var v = row.Cell(snc).GetString().Trim();
                if (!string.IsNullOrEmpty(v)) p.SipDisplayName = v;
            }
            if (sipLineCol is int slc)
            {
                var cell = row.Cell(slc);
                if (cell.DataType == XLDataType.Number)
                    p.SipLineIndex = (int)Math.Max(1, Math.Round(cell.GetDouble()));
                else if (int.TryParse(cell.GetString().Trim(), out var n) && n >= 1)
                    p.SipLineIndex = n;
            }
            if (sipExtCol is int sec)
            {
                var v = row.Cell(sec).GetString().Trim();
                if (!string.IsNullOrEmpty(v)) p.SipExtension = v;
            }
            if (sipRegCol is int src)
            {
                var cell = row.Cell(src);
                if (cell.DataType == XLDataType.Boolean)
                    p.SipRegistrationEnabled = cell.GetBoolean();
                else
                {
                    var s = cell.GetString().Trim().ToLowerInvariant();
                    if (s is "true" or "1" or "yes" or "y" or "enabled" or "on")
                        p.SipRegistrationEnabled = true;
                    else if (s is "false" or "0" or "no" or "n" or "disabled" or "off")
                        p.SipRegistrationEnabled = false;
                }
            }

            phones.Add(p);
        }
        return phones;
    }
}
