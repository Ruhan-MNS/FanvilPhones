using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MNS.SingleSignOn;

public sealed class ConfigureSingleSignOnCookie(IOptions<SingleSignOnOptions> settings, IOptions<IdentityOptions> identity,
    TimeProvider clock) : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name != IdentityConstants.ApplicationScheme || !settings.Value.Enabled) return;
        var config = settings.Value;
        options.Cookie.Name = SingleSignOnOptions.CookieName;
        options.Cookie.Domain = config.CookieDomain;
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(config.IdleMinutes);
        options.SlidingExpiration = true;
        options.TimeProvider = clock;
        var onSigningIn = options.Events.OnSigningIn;
        var onSigningOut = options.Events.OnSigningOut;
        var onValidate = options.Events.OnValidatePrincipal;
        options.Events.OnSigningIn = async context =>
        {
            await onSigningIn(context);
            if (!context.Request.IsHttps) throw new InvalidOperationException("Shared sign-in requires HTTPS.");
            var id = context.Principal?.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
            if (string.IsNullOrWhiteSpace(id) || context.Principal?.Identity is not ClaimsIdentity claims)
                throw new InvalidOperationException("Shared sign-in requires an identified account.");
            var sessions = context.HttpContext.RequestServices.GetRequiredService<ISingleSignOnSessions>();
            var oldId = context.HttpContext.User.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
            var oldSession = context.HttpContext.User.FindFirstValue(SingleSignOnOptions.SessionClaim);
            DateTimeOffset? expires = null;
            if (oldId == id && oldSession is not null)
            {
                expires = await sessions.GetExpiryAsync(id, oldSession, context.HttpContext.RequestAborted);
                // A principal refresh racing with logout must never create a replacement session.
                if (expires is null) throw new InvalidOperationException("The shared session has ended. Sign in again to continue.");
            }
            if (oldId is not null && oldSession is not null && oldId != id)
                await sessions.RevokeAsync(oldId, oldSession, context.HttpContext.RequestAborted);
            var session = expires is null ? Guid.NewGuid().ToString("N") : oldSession!;
            if (expires is null)
            {
                expires = clock.GetUtcNow().AddHours(config.MaximumSessionHours);
                await sessions.CreateAsync(id, session, expires.Value, context.HttpContext.RequestAborted);
            }
            foreach (var claim in claims.FindAll(SingleSignOnOptions.SessionClaim).ToArray()) claims.RemoveClaim(claim);
            claims.AddClaim(new(SingleSignOnOptions.SessionClaim, session));
            if (context.Properties.ExpiresUtc > expires) context.Properties.ExpiresUtc = expires;
            Log(context.HttpContext, 2100, "Shared sign-in session established.");
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            // Identity may regenerate role claims during security-stamp validation.
            var previous = context.Principal;
            var session = previous?.FindFirstValue(SingleSignOnOptions.SessionClaim);
            var previousId = previous?.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
            await onValidate(context);
            if (context.Principal?.Identity?.IsAuthenticated != true) return;
            var userId = context.Principal.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
            var expiry = userId == previousId && userId is not null && session is not null
                ? await context.HttpContext.RequestServices.GetRequiredService<ISingleSignOnSessions>()
                    .GetExpiryAsync(userId, session, context.HttpContext.RequestAborted) : null;
            if (expiry is null)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                Log(context.HttpContext, 2101, "Shared sign-in session rejected or expired.");
                return;
            }
            if (!context.Principal.HasClaim(x => x.Type == SingleSignOnOptions.SessionClaim)
                && context.Principal.Identity is ClaimsIdentity refreshed) refreshed.AddClaim(new(SingleSignOnOptions.SessionClaim, session!));
        };
        options.Events.OnSigningOut = async context =>
        {
            var id = context.HttpContext.User.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
            var session = context.HttpContext.User.FindFirstValue(SingleSignOnOptions.SessionClaim);
            if (id is not null && session is not null)
                await context.HttpContext.RequestServices.GetRequiredService<ISingleSignOnSessions>()
                    .RevokeAsync(id, session, context.HttpContext.RequestAborted);
            await onSigningOut(context);
            Log(context.HttpContext, 2102, "Shared sign-in session ended.");
        };
    }

    private static void Log(HttpContext http, int eventId, string message) =>
        http.RequestServices.GetRequiredService<ILogger<ConfigureSingleSignOnCookie>>().LogInformation(new EventId(eventId), "{SsoEvent}", message);
}
