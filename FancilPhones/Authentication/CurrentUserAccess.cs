using FancilPhones.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace FancilPhones.Authentication;

public sealed class CurrentUserAccess(
    IHttpContextAccessor httpContext,
    AuthenticationStateProvider authenticationState,
    IAuthorizationService authorization,
    ILogger<CurrentUserAccess> logger) : IUserAccess
{
    public async Task RequireAuthenticatedAsync()
    {
        var user = await GetUserAsync();
        if (user.Identity?.IsAuthenticated != true)
        {
            logger.LogWarning("Phone data access denied: authentication is required.");
            throw new UnauthorizedAccessException("Sign in to access phone data.");
        }
    }

    public async Task RequireAdministratorAsync()
    {
        var user = await GetUserAsync();
        if (!(await authorization.AuthorizeAsync(user, PhonePolicies.Administrator)).Succeeded)
        {
            logger.LogWarning("Phone operation denied by policy {Policy}. Authenticated: {Authenticated}.",
                PhonePolicies.Administrator, user.Identity?.IsAuthenticated == true);
            throw new UnauthorizedAccessException("Phone administrator access is required.");
        }
    }

    private async Task<ClaimsPrincipal> GetUserAsync()
    {
        // HTTP endpoints have a request principal; interactive circuits use their revalidated state.
        if (httpContext.HttpContext is { } context && !context.Response.HasStarted)
            return context.User;
        return (await authenticationState.GetAuthenticationStateAsync()).User;
    }
}
