using FancilPhones.Components;
using FancilPhones.Application;
using FancilPhones.Authentication;
using FancilPhones.Endpoints;
using FancilPhones.Infrastructure;
using FancilPhones.Infrastructure.Data;
using FancilPhones.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using FancilPhones.UI;
using Radzen;
using System.Threading.RateLimiting;

var migrateOnly = args.Contains("--migrate-database", StringComparer.OrdinalIgnoreCase);
var importIndex = Array.FindIndex(args, a => string.Equals(a, "--import-sqlite", StringComparison.OrdinalIgnoreCase));
if (importIndex >= 0 && (importIndex + 1 >= args.Length || args[importIndex + 1].StartsWith("--")))
    throw new ArgumentException("--import-sqlite requires the path to the legacy database.");
var hostArgs = args.Where((arg, index) => !string.Equals(arg, "--migrate-database", StringComparison.OrdinalIgnoreCase)
    && index != importIndex && (importIndex < 0 || index != importIndex + 1)).ToArray();
var builder = WebApplication.CreateBuilder(hostArgs);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<UiFeedback>();
builder.Services.AddRadzenComponents();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUserAccess, CurrentUserAccess>();

// Identity settings and stores match BIDevHub's shared ASP_AUTH model.
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();
builder.Services.AddIdentityCore<AppUser>(options =>
{
    options.User.RequireUniqueEmail = false;
    options.SignIn.RequireConfirmedAccount = false;
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
}).AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    // Separate app session: sharing the identity store does not imply sharing cookies or keys.
    options.Cookie.Name = "FancilPhones.Identity";
    options.LoginPath = "/login";
    options.LogoutPath = "/auth/logout";
    options.AccessDeniedPath = "/access-denied";
    options.SlidingExpiration = true;
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme,
    options => options.Cookie.Name = "FancilPhones.TwoFactor");
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorRememberMeScheme,
    options => options.Cookie.Name = "FancilPhones.RememberDevice");
builder.Services.AddAuthorization(options => options.AddPolicy(PhonePolicies.Administrator,
    policy => policy.RequireAuthenticatedUser().RequireRole("BI-ADMIN", "IT_ADMIN")));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
        }));
});

var app = builder.Build();
if (migrateOnly || importIndex >= 0 || builder.Configuration.GetValue("Database:ApplyMigrationsOnStartup", true))
{
    await using var scope = app.Services.CreateAsyncScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    var stopping = app.Lifetime.ApplicationStopping;
    await using var db = await factory.CreateDbContextAsync(stopping);
    // Only the FancilPhones schema is migrated. Never migrate/seed the shared auth store.
    try
    {
        app.Logger.LogInformation("Checking phone store migrations for schema {Schema}.", AppDbContext.Schema);
        await db.Database.MigrateAsync(stopping);
        app.Logger.LogInformation("Phone store migrations completed.");
        if (importIndex >= 0)
            await scope.ServiceProvider.GetRequiredService<LegacyPhoneDataImporter>().ImportAsync(args[importIndex + 1], stopping);
    }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
    {
        app.Logger.LogInformation("Phone store initialization cancelled during application shutdown.");
        return;
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Phone store initialization failed; application startup cannot continue.");
        throw;
    }
}
if (migrateOnly || importIndex >= 0) return;

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
//app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapPhoneExportEndpoints();
app.MapPhoneAccountEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Fanvil Phones started in {Environment}.", app.Environment.EnvironmentName));
app.Lifetime.ApplicationStopping.Register(() => app.Logger.LogInformation("Fanvil Phones is stopping."));
app.Run();

public partial class Program;
