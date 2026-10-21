using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MNS.SingleSignOn;

public static class SingleSignOnExtensions
{
    // Existing launch links may point at a login page. A valid shared session should enter the application directly.
    public static IApplicationBuilder UseMnsSingleSignOnLoginRedirect(this IApplicationBuilder app, string loginPath, string landingPath)
    {
        return app.Use(async (context, next) =>
        {
            var settings = context.RequestServices.GetRequiredService<IOptions<SingleSignOnOptions>>().Value;
            if (settings.Enabled && HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals(new PathString(loginPath))
                && context.User.Identity?.IsAuthenticated == true)
            {
                var target = context.Request.Query["returnUrl"].FirstOrDefault();
                if (!IsLocalUrl(target)) target = context.Request.PathBase + landingPath;
                await Results.LocalRedirect(target!).ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }

    private static bool IsLocalUrl(string? url) => !string.IsNullOrEmpty(url) && !url.Any(char.IsControl)
        && ((url[0] == '/' && (url.Length == 1 || url[1] is not ('/' or '\\')))
            || (url.StartsWith("~/", StringComparison.Ordinal) && (url.Length == 2 || url[2] is not ('/' or '\\'))));

    public static IServiceCollection AddMnsSingleSignOn<TContext>(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment) where TContext : DbContext
    {
        var settings = configuration.GetSection(SingleSignOnOptions.Section).Get<SingleSignOnOptions>() ?? new();
        settings.Validate(environment.ContentRootPath);
        services.AddOptions<SingleSignOnOptions>().BindConfiguration(SingleSignOnOptions.Section).ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ISingleSignOnSessions, SingleSignOnSessions<TContext>>();
        services.AddScoped<SingleSignOnSessionValidator>();
        services.AddSingleton<IPostConfigureOptions<CookieAuthenticationOptions>, ConfigureSingleSignOnCookie>();
        if (!settings.Enabled) return services;

        var protection = services.AddDataProtection().SetApplicationName(SingleSignOnOptions.ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(settings.KeyRingPath));
        if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
            protection.ProtectKeysWithCertificate(settings.CertificateThumbprint);
        else
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey) { certificate.Dispose(); throw new InvalidOperationException("The SSO key-protection certificate must include its private key."); }
            protection.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
            // Resolve through a factory so DI owns disposal of the certificate.
            services.AddSingleton<X509Certificate2>(_ => certificate);
            services.AddOptions<KeyManagementOptions>().Configure<X509Certificate2>((_, _) => { });
        }
        return services;
    }
}
