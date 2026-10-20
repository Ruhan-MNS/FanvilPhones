using FancilPhones.Application;

namespace FancilPhones.Endpoints;

public static class PhoneExportEndpoints
{
    public static void MapPhoneExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // CSV preview — requires a signed-in employee.
        endpoints.MapGet("/api/phonebook.csv", async (IPhoneDirectory directory) =>
        {
            var contacts = await directory.GetContactsAsync();
            var bytes = PhonebookCsv.Build(contacts);
            return Results.File(bytes, "text/csv", "phonebook.csv");
        }).RequireAuthorization();

        // XLSX export — same data, formatted as an Excel workbook.
        endpoints.MapGet("/api/phonebook.xlsx", async (IPhoneDirectory directory) =>
        {
            var contacts = await directory.GetContactsAsync();

            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Contacts");

            string[] headers = { "Display name", "Office", "Mobile", "Other", "Group", "Ring", "Updated" };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#E2231A");
            header.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
            header.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;

            int row = 2;
            foreach (var c in contacts)
            {
                ws.Cell(row, 1).Value = c.DisplayName;
                ws.Cell(row, 2).Value = c.OfficeNumber ?? "";
                ws.Cell(row, 3).Value = c.MobileNumber ?? "";
                ws.Cell(row, 4).Value = c.OtherNumber ?? "";
                ws.Cell(row, 5).Value = c.Group ?? "";
                ws.Cell(row, 6).Value = c.Ring ?? "";
                ws.Cell(row, 7).Value = c.UpdatedAt.ToLocalTime();
                ws.Cell(row, 7).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
                row++;
            }

            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var fileName = $"phonebook-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
            return Results.File(ms.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }).RequireAuthorization();

        // Phones XLSX export — backup/restore the fleet config.
        endpoints.MapGet("/api/phones.xlsx", async (IPhoneDirectory directory) =>
        {
            var phones = await directory.GetPhonesAsync();

            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.Worksheets.Add("Phones");

            string[] headers =
            {
                "Name", "IP", "Scheme", "Username", "Password",
                "UploadPath", "UploadFieldName", "Enabled",
                "SipDisplayName", "SipLineIndex", "SipExtension", "SipRegistrationEnabled"
            };
            for (int i = 0; i < headers.Length; i++)
                ws.Cell(1, i + 1).Value = headers[i];

            var header = ws.Range(1, 1, 1, headers.Length);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#E2231A");
            header.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
            header.Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Left;

            int row = 2;
            foreach (var p in phones)
            {
                ws.Cell(row, 1).Value = p.Name;
                ws.Cell(row, 2).Value = p.IpAddress;
                ws.Cell(row, 3).Value = p.Scheme;
                ws.Cell(row, 4).Value = p.Username;
                ws.Cell(row, 5).Value = p.Password;
                ws.Cell(row, 6).Value = p.UploadPath;
                ws.Cell(row, 7).Value = p.UploadFieldName;
                ws.Cell(row, 8).Value = p.Enabled;
                ws.Cell(row, 9).Value = p.SipDisplayName ?? "";
                ws.Cell(row, 10).Value = p.SipLineIndex;
                ws.Cell(row, 11).Value = p.SipExtension ?? "";
                ws.Cell(row, 12).Value = p.SipRegistrationEnabled;
                row++;
            }

            ws.Columns().AdjustToContents();
            ws.SheetView.FreezeRows(1);
            ws.RangeUsed()?.SetAutoFilter();

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            var fileName = $"phones-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
            return Results.File(ms.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        }).RequireAuthorization(PhonePolicies.Administrator);
    }
}
