using FancilPhones.Infrastructure.Data;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SignInResult = Microsoft.AspNetCore.Identity.SignInResult;

namespace FancilPhones.Authentication
{
    internal static class IdentityComponentsEndpointRouteBuilderExtensions
    {
        // This host only provides sign-in and sign-out; account provisioning is handled elsewhere.
        public static IEndpointConventionBuilder MapPhoneAccountEndpoints(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            var accountGroup = endpoints.MapGroup("/auth");

            accountGroup.MapPost("/login", async (
                HttpContext context,
                [FromServices] SignInManager<AppUser> signInManager,
                [FromServices] ILoggerFactory loggerFactory,
                [FromServices] IAntiforgery antiforgery) =>
            {
                var logger = loggerFactory.CreateLogger("AccountLogin");
                if (!await ValidateAntiforgeryAsync(context, antiforgery, logger)) return Results.BadRequest();
                var form = await context.Request.ReadFormAsync();
                var userName = form["Username"].ToString().Trim();
                var password = form["Password"].ToString();
                var rememberMe = string.Equals(form["RememberMe"], "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form["RememberMe"], "on", StringComparison.OrdinalIgnoreCase);
                var returnUrl = form["ReturnUrl"].ToString();

                if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
                {
                    return LoginRedirect(context, returnUrl, "invalid");
                }

                var result = await signInManager.PasswordSignInAsync(
                    userName,
                    password,
                    rememberMe,
                    lockoutOnFailure: true);

                LogSignInResult(logger, result, "Password");

                if (result.RequiresTwoFactor)
                {
                    var twoFactorUrl = UriHelper.BuildRelative(
                        context.Request.PathBase,
                        "/login/two-factor",
                        QueryString.Create(new Dictionary<string, string?>
                        {
                            ["ReturnUrl"] = LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/",
                            ["RememberMe"] = rememberMe.ToString()
                        }));

                    return Results.LocalRedirect(twoFactorUrl);
                }

                if (result.IsLockedOut)
                {
                    return Results.LocalRedirect("/login?error=locked");
                }

                if (!result.Succeeded)
                {
                    return LoginRedirect(context, returnUrl, "invalid");
                }

                var safeReturnUrl = LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/";
                return Results.LocalRedirect(safeReturnUrl);
            }).RequireRateLimiting("authentication");

            accountGroup.MapPost("/two-factor", async (
                HttpContext context,
                [FromServices] SignInManager<AppUser> signInManager,
                [FromServices] ILoggerFactory loggerFactory,
                [FromServices] IAntiforgery antiforgery) =>
            {
                var logger = loggerFactory.CreateLogger("AccountLogin");
                if (!await ValidateAntiforgeryAsync(context, antiforgery, logger)) return Results.BadRequest();
                var form = await context.Request.ReadFormAsync();
                var returnUrl = form["ReturnUrl"].ToString();
                var rememberMe = string.Equals(form["RememberMe"], "true", StringComparison.OrdinalIgnoreCase);
                var rememberMachine = string.Equals(form["RememberMachine"], "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(form["RememberMachine"], "on", StringComparison.OrdinalIgnoreCase);
                var code = form["TwoFactorCode"].ToString().Replace(" ", string.Empty).Replace("-", string.Empty);

                if (string.IsNullOrWhiteSpace(code)
                    || await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
                {
                    return Results.LocalRedirect(LoginRedirectPath(context, returnUrl, "invalid"));
                }

                var result = await signInManager.TwoFactorAuthenticatorSignInAsync(code, rememberMe, rememberMachine);
                LogSignInResult(logger, result, "Authenticator");
                if (result.Succeeded)
                {
                    return Results.LocalRedirect(LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/");
                }

                if (result.IsLockedOut)
                {
                    return Results.LocalRedirect("/login?error=locked");
                }

                var twoFactorUrl = UriHelper.BuildRelative(
                    context.Request.PathBase,
                    "/login/two-factor",
                    QueryString.Create(new Dictionary<string, string?>
                    {
                        ["ReturnUrl"] = LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/",
                        ["RememberMe"] = rememberMe.ToString(),
                        ["error"] = "invalid"
                    }));

                return Results.LocalRedirect(twoFactorUrl);
            }).RequireRateLimiting("authentication");

            accountGroup.MapPost("/recovery-code", async (
                HttpContext context,
                [FromServices] SignInManager<AppUser> signInManager,
                [FromServices] ILoggerFactory loggerFactory,
                [FromServices] IAntiforgery antiforgery) =>
            {
                var logger = loggerFactory.CreateLogger("AccountLogin");
                if (!await ValidateAntiforgeryAsync(context, antiforgery, logger)) return Results.BadRequest();
                var form = await context.Request.ReadFormAsync();
                var returnUrl = form["ReturnUrl"].ToString();
                var code = form["RecoveryCode"].ToString().Replace(" ", string.Empty);

                if (string.IsNullOrWhiteSpace(code)
                    || await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
                {
                    return LoginRedirect(context, returnUrl, "invalid");
                }

                var result = await signInManager.TwoFactorRecoveryCodeSignInAsync(code);
                LogSignInResult(logger, result, "RecoveryCode");
                if (result.Succeeded)
                {
                    return Results.LocalRedirect(LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/");
                }
                if (result.IsLockedOut)
                {
                    return Results.LocalRedirect("/login?error=locked");
                }

                var recoveryUrl = UriHelper.BuildRelative(
                    context.Request.PathBase,
                    "/login/recovery-code",
                    QueryString.Create(new Dictionary<string, string?>
                    {
                        ["ReturnUrl"] = LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/",
                        ["error"] = "invalid"
                    }));
                return Results.LocalRedirect(recoveryUrl);
            }).RequireRateLimiting("authentication");

            accountGroup.MapPost("/logout", async (
                HttpContext context,
                [FromServices] SignInManager<AppUser> signInManager,
                [FromServices] ILoggerFactory loggerFactory,
                [FromServices] IAntiforgery antiforgery) =>
            {
                var logger = loggerFactory.CreateLogger("AccountLogin");
                if (!await ValidateAntiforgeryAsync(context, antiforgery, logger)) return Results.BadRequest();
                var form = await context.Request.ReadFormAsync();
                var returnUrl = form["ReturnUrl"].ToString();
                var destination = returnUrl.StartsWith('/') ? returnUrl : $"/{returnUrl}";
                await signInManager.SignOutAsync();
                logger.LogInformation("User signed out.");
                return Results.LocalRedirect(LocalReturnUrl.IsLocal(destination) ? destination : "/");
            });

            return accountGroup;
        }

        private static void LogSignInResult(ILogger logger, SignInResult result, string method)
        {
            if (result.Succeeded) logger.LogInformation("Authentication succeeded using {Method}.", method);
            else if (result.RequiresTwoFactor) logger.LogInformation("Second factor required.");
            else logger.LogWarning("Authentication failed using {Method}. Locked out: {LockedOut}; not allowed: {NotAllowed}.", method, result.IsLockedOut, result.IsNotAllowed);
        }

        private static async Task<bool> ValidateAntiforgeryAsync(HttpContext context, IAntiforgery antiforgery, ILogger logger)
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                return true;
            }
            catch (AntiforgeryValidationException)
            {
                logger.LogWarning("Authentication request rejected: invalid antiforgery token.");
                return false;
            }
        }

        private static IResult LoginRedirect(HttpContext context, string returnUrl, string error)
        {
            return Results.LocalRedirect(LoginRedirectPath(context, returnUrl, error));
        }

        private static string LoginRedirectPath(HttpContext context, string returnUrl, string error)
        {
            var query = QueryString.Create(new Dictionary<string, string?>
            {
                ["ReturnUrl"] = LocalReturnUrl.IsLocal(returnUrl) ? returnUrl : "/",
                ["error"] = error
            });

            return UriHelper.BuildRelative(context.Request.PathBase, "/login", query);
        }

    }
}
