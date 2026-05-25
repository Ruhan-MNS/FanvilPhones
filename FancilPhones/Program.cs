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

var app = builder.Build();

// Ensure DB + seed admin/admin + roles.
using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

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
