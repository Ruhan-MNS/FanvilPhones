using FancilPhones.Components;
using FancilPhones.Data;
using FancilPhones.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var connStr = builder.Configuration.GetConnectionString("Default")
              ?? "Data Source=fancilphones.db";

// EF: factory (used by Blazor pages) + scoped wrapper (used by Identity).
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connStr));
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// Identity (cookie-based, no UI scaffolding — we render our own login).
builder.Services
    .AddIdentityCore<AppUser>(o =>
    {
        // Relaxed password policy: small in-house tool. Tighten if exposed publicly.
        o.Password.RequiredLength = 4;
        o.Password.RequireDigit = false;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<PhoneSyncService>();
builder.Services.AddScoped<PhoneDiscoveryService>();

var app = builder.Build();

// Ensure DB + seed admin/admin + roles.
using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // Additive columns on existing DBs (project uses EnsureCreated, no migrations).
    // Check first via PRAGMA so re-runs don't log a failed ALTER on every startup.
    async Task AddColumnIfMissing(string table, string column, string definition)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        await reader.CloseAsync();
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    await AddColumnIfMissing("Phones",   "SipDisplayName",          "TEXT NULL");
    await AddColumnIfMissing("Phones",   "SipLineIndex",            "INTEGER NOT NULL DEFAULT 1");
    await AddColumnIfMissing("Phones",   "SipExtension",            "TEXT NULL");
    await AddColumnIfMissing("Phones",   "SipRegistrationEnabled",  "INTEGER NOT NULL DEFAULT 1");
    await AddColumnIfMissing("SyncRuns", "Action",                  "TEXT NOT NULL DEFAULT 'Sync'");

    var rm = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Admin", "Technician" })
        if (!await rm.RoleExistsAsync(role))
            await rm.CreateAsync(new IdentityRole(role));

    var um = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    if (await um.FindByNameAsync("admin") is null)
    {
        var u = new AppUser { UserName = "admin", MustChangePassword = true };
        var created = await um.CreateAsync(u, "admin");
        if (created.Succeeded)
            await um.AddToRoleAsync(u, "Admin");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

// CSV preview — leave open (it's just the master list, no secrets).
app.MapGet("/api/phonebook.csv", async (IDbContextFactory<AppDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync();
    var bytes = PhonebookCsv.Build(contacts);
    return Results.File(bytes, "text/csv", "phonebook.csv");
});

// XLSX export — same data, formatted as an Excel workbook.
app.MapGet("/api/phonebook.xlsx", async (IDbContextFactory<AppDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync();

    using var wb = new ClosedXML.Excel.XLWorkbook();
    var ws = wb.Worksheets.Add("Contacts");

    string[] headers = { "Display name", "Office", "Mobile", "Other", "Group", "Ring", "Updated" };
    for (int i = 0; i < headers.Length; i++)
        ws.Cell(1, i + 1).Value = headers[i];

    var header = ws.Range(1, 1, 1, headers.Length);
    header.Style.Font.Bold = true;
    header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#4F46E5");
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
});

// Phones XLSX export — backup/restore the fleet config.
app.MapGet("/api/phones.xlsx", async (IDbContextFactory<AppDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    var phones = await db.Phones.OrderBy(p => p.Name).ToListAsync();

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
    header.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromHtml("#4F46E5");
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
}).RequireAuthorization();

// ---- Auth endpoints (form-post; no antiforgery so the static-rendered login form works) ----
app.MapPost("/auth/login", async (
    HttpContext ctx,
    SignInManager<AppUser> sm,
    UserManager<AppUser> um,
    [FromForm] string username,
    [FromForm] string password,
    [FromForm] string? returnUrl) =>
{
    var user = await um.FindByNameAsync(username ?? "");
    if (user is null)
        return Results.Redirect("/login?error=1");

    var result = await sm.PasswordSignInAsync(user, password ?? "", isPersistent: true, lockoutOnFailure: false);
    if (!result.Succeeded)
        return Results.Redirect("/login?error=1");

    if (user.MustChangePassword)
        return Results.Redirect("/account/change-password");

    var target = !string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        ? returnUrl!
        : "/";
    return Results.Redirect(target);
}).DisableAntiforgery();

app.MapPost("/auth/logout", async (HttpContext ctx, SignInManager<AppUser> sm) =>
{
    await sm.SignOutAsync();
    return Results.Redirect("/login");
}).DisableAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
