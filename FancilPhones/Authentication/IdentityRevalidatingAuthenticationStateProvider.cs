using FancilPhones.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace FancilPhones.Authentication
{
    // This is a server-side AuthenticationStateProvider that revalidates the security stamp for the connected user
    // every 30 minutes an interactive circuit is connected.
    internal sealed class IdentityRevalidatingAuthenticationStateProvider(
            ILoggerFactory loggerFactory,
            IServiceScopeFactory scopeFactory,
            IOptions<IdentityOptions> options,
            ILogger<IdentityRevalidatingAuthenticationStateProvider> logger)
        : RevalidatingServerAuthenticationStateProvider(loggerFactory)
    {
        protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

        protected override async Task<bool> ValidateAuthenticationStateAsync(
            AuthenticationState authenticationState, CancellationToken cancellationToken)
        {
            // Get the user manager from a new scope to ensure it fetches fresh data
            await using var scope = scopeFactory.CreateAsyncScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var principal = authenticationState.User;
            var userId = userManager.GetUserId(principal);
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            // Project only the stamp and pass cancellation into the actual EF query.
            var user = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => new { u.SecurityStamp }).SingleOrDefaultAsync(cancellationToken);
            return ValidateSecurityStamp(userManager, principal, user?.SecurityStamp, user is not null);
        }

        private bool ValidateSecurityStamp(UserManager<AppUser> userManager, ClaimsPrincipal principal, string? userStamp, bool userExists)
        {
            if (!userExists)
            {
                logger.LogInformation("Connected session invalidated: the shared account no longer exists.");
                return false;
            }
            else if (!userManager.SupportsUserSecurityStamp)
            {
                return true;
            }
            else
            {
                var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
                if (principalStamp != userStamp)
                    logger.LogInformation("Connected session invalidated: the shared security stamp has changed.");
                return principalStamp == userStamp;
            }
        }
    }
}
