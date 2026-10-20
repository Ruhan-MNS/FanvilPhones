using System.Net;
using System.Text.RegularExpressions;
using FancilPhones.Authentication;
using FancilPhones.Infrastructure.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FancilPhones.Tests;

public sealed class AuthenticationTests
{
    [Theory]
    [InlineData("/phones", true)]
    [InlineData("/", true)]
    [InlineData("https://example.com", false)]
    [InlineData("//example.com", false)]
    [InlineData("/\\example.com", false)]
    [InlineData("/\nexample.com", false)]
    [InlineData("phones", false)]
    public void ReturnUrlsMustBeLocal(string url, bool expected) => Assert.Equal(expected, LocalReturnUrl.IsLocal(url));

    [Fact]
    public async Task LoginAndLogoutRejectRequestsWithoutAntiforgeryToken()
    {
        await using var app = new PhoneWebApp();
        using var client = app.CreateClient(Options);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/login", Form([]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", Form([]))).StatusCode);
    }

    [Fact]
    public async Task InvalidCredentialsPreserveSafeDestinationAndNeverRedirectExternally()
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync();
        using var client = app.CreateClient(Options);
        var response = await LoginAsync(client, "missing", "/phones");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("ReturnUrl=%2Fphones", response.Headers.Location!.OriginalString);
        var unsafeResponse = await LoginAsync(client, "missing", "//example.com");
        Assert.Contains("ReturnUrl=%2F&", unsafeResponse.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("BI-ADMIN", HttpStatusCode.OK)]
    [InlineData("IT_ADMIN", HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.Forbidden)]
    public async Task SharedEmployeeAccountsSignInAndExportHonorsRoles(string? role, HttpStatusCode expected)
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync(role);
        using var client = app.CreateClient(Options);
        var response = await LoginAsync(client, "EMP001", "/phones");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/phones", response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("FancilPhones.Identity="));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/phonebook.csv")).StatusCode);
        Assert.Equal(expected, (await client.GetAsync("/api/phones.xlsx")).StatusCode);
        // SSR also exercises the page/service authorization boundary against the request principal.
        foreach (var path in new[] { "/", "/phones", "/contacts", "/sync", "/logs", "/account/change-password" })
        {
            var page = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains("workspace-header", html);
            Assert.Contains("Radzen.Blazor", html);
            Assert.DoesNotContain("MudBlazor", html);
        }
    }

    [Theory]
    [InlineData("employee@multinetsystems.co.za", "employee@multinetsystems.co.za", "E")]
    [InlineData(null, "EMP001", "E")]
    public async Task HeaderDisplaysSharedAccountEmailWithUsernameFallback(string? email, string expectedLabel, string expectedInitial)
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync(email: email);
        using var client = app.CreateClient(Options);
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client, "EMP001", "/")).StatusCode);

        var html = await client.GetStringAsync("/");
        var account = Regex.Match(html, "<a class=\"account-link\"[^>]*>.*?</a>", RegexOptions.Singleline);
        Assert.True(account.Success);
        Assert.Contains($"<strong>{expectedLabel}</strong>", account.Value);
        Assert.Contains($"title=\"{expectedLabel}\"", account.Value);
        Assert.Contains($"aria-label=\"Manage account for {expectedLabel}\"", account.Value);
        Assert.Contains($"aria-hidden=\"true\">{expectedInitial}</span>", account.Value);
        if (email is not null) Assert.DoesNotContain("EMP001", account.Value);
    }

    [Fact]
    public async Task TwoFactorAccountsRequireSecondFactorAndAcceptRecoveryCode()
    {
        await using var app = new PhoneWebApp();
        var recovery = await app.InitializeAsync(twoFactor: true);
        using var client = app.CreateClient(Options);
        var response = await LoginAsync(client, "EMP001", "/phones");
        Assert.StartsWith("/login/two-factor?", response.Headers.Location!.OriginalString);
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("FancilPhones.Identity="));
        var token = await GetTokenAsync(client, "/login/recovery-code");
        var verified = await client.PostAsync("/auth/recovery-code", Form(new()
        {
            ["__RequestVerificationToken"] = token,
            ["RecoveryCode"] = recovery!, ["ReturnUrl"] = "/phones"
        }));
        Assert.Equal("/phones", verified.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/phonebook.csv")).StatusCode);
    }

    [Fact]
    public async Task FailedPasswordsLockTheSharedAccount()
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync();
        using var client = app.CreateClient(Options);
        HttpResponseMessage response = null!;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            response = await client.PostAsync("/auth/login", Form(new()
            {
                ["__RequestVerificationToken"] = await GetTokenAsync(client, "/login"),
                ["Username"] = "EMP001", ["Password"] = "wrong"
            }));
        }
        Assert.Equal("/login?error=locked", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CentralSecurityStampChangeInvalidatesTheSession()
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync();
        using var client = app.CreateClient(Options);
        await LoginAsync(client, "EMP001", "/");
        using (var scope = app.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = await manager.FindByNameAsync("EMP001");
            Assert.True((await manager.UpdateSecurityStampAsync(user!)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/phonebook.csv")).StatusCode);
    }

    [Fact]
    public async Task ProtectedLogoutClearsTheApplicationSession()
    {
        await using var app = new PhoneWebApp();
        await app.InitializeAsync();
        using var client = app.CreateClient(Options);
        await LoginAsync(client, "EMP001", "/");
        var response = await client.PostAsync("/auth/logout", Form(new()
        {
            ["__RequestVerificationToken"] = await GetTokenAsync(client, "/")
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/phonebook.csv")).StatusCode);
    }

    [Fact]
    public async Task PhoneExportsRequireAuthentication()
    {
        await using var app = new PhoneWebApp();
        using var client = app.CreateClient(Options);
        foreach (var path in new[] { "/api/phonebook.csv", "/api/phonebook.xlsx", "/api/phones.xlsx" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    private static readonly WebApplicationFactoryClientOptions Options = new()
    {
        AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
    };

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string returnUrl) =>
        await client.PostAsync("/auth/login", Form(new()
        {
            ["__RequestVerificationToken"] = await GetTokenAsync(client, "/login"),
            ["Username"] = username, ["Password"] = "ValidPass123!", ["ReturnUrl"] = returnUrl
        }));

    private static FormUrlEncodedContent Form(Dictionary<string, string> fields) => new(fields);

    private static async Task<string> GetTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Rendered form must contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed class PhoneWebApp : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection auth = new("Data Source=:memory:");
        private readonly SqliteConnection phones = new("Data Source=:memory:");

        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Supply early configuration before the minimal host registers Infrastructure.
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ASP_Auth"] = "Server=unused;Database=ASP_AUTH;Integrated Security=True",
                ["ConnectionStrings:MNSGlobalDev"] = "Server=unused;Database=MNSGlobalDev;Integrated Security=True",
                ["Database:ApplyMigrationsOnStartup"] = "false"
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            auth.Open();
            phones.Open();
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ASP_Auth"] = "Server=unused;Database=ASP_AUTH;Integrated Security=True",
                ["ConnectionStrings:MNSGlobalDev"] = "Server=unused;Database=MNSGlobalDev;Integrated Security=True",
                ["Database:ApplyMigrationsOnStartup"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                ReplaceFactory<AuthDbContext>(services, auth);
                ReplaceFactory<AppDbContext>(services, phones);
                services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
            });
        }

        public async Task<string?> InitializeAsync(string? role = null, bool twoFactor = false, string? email = null)
        {
            using var scope = Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
            await using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = "EMP001", Email = email, LockoutEnabled = true };
            Assert.True((await manager.CreateAsync(user, "ValidPass123!")).Succeeded);
            if (role is not null)
            {
                await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().CreateAsync(new IdentityRole(role));
                Assert.True((await manager.AddToRoleAsync(user, role)).Succeeded);
            }
            if (!twoFactor) return null;
            await manager.ResetAuthenticatorKeyAsync(user);
            await manager.SetTwoFactorEnabledAsync(user, true);
            return (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 1))!.Single();
        }

        private static void ReplaceFactory<T>(IServiceCollection services, SqliteConnection connection) where T : DbContext
        {
            services.RemoveAll<IDbContextFactory<T>>();
            services.RemoveAll<DbContextOptions<T>>();
            services.RemoveAll<IDbContextOptionsConfiguration<T>>();
            services.AddDbContextFactory<T>(options => options.UseSqlite(connection));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            await auth.DisposeAsync();
            await phones.DisposeAsync();
        }
    }
}
