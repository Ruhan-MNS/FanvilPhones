using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MNS.SingleSignOn;

public interface ISingleSignOnSessions
{
    Task CreateAsync(string userId, string sessionId, DateTimeOffset expires, CancellationToken ct);
    Task<DateTimeOffset?> GetExpiryAsync(string userId, string sessionId, CancellationToken ct);
    Task RevokeAsync(string userId, string sessionId, CancellationToken ct);
}

// ASP_AUTH already owns AspNetUserTokens in every participating Identity model.
// These rows are session metadata, not passwords or copies of authentication cookies.
public sealed class SingleSignOnSessions<TContext>(TContext db, TimeProvider clock) : ISingleSignOnSessions where TContext : DbContext
{
    public const string Provider = "MNS.SharedSso.v1";
    private DbSet<IdentityUserToken<string>> Tokens => db.Set<IdentityUserToken<string>>();

    public async Task CreateAsync(string userId, string sessionId, DateTimeOffset expires, CancellationToken ct)
    {
        var existing = await Tokens.AsNoTracking().Where(x => x.UserId == userId && x.LoginProvider == Provider).ToArrayAsync(ct);
        var expired = existing.Where(x => !TryExpiry(x.Value, out var expiry) || expiry <= clock.GetUtcNow()).Select(x => x.Name).ToArray();
        if (expired.Length > 0)
            await Tokens.Where(x => x.UserId == userId && x.LoginProvider == Provider && expired.Contains(x.Name)).ExecuteDeleteAsync(ct);
        Tokens.Add(new() { UserId = userId, LoginProvider = Provider, Name = sessionId,
            Value = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) });
        await db.SaveChangesAsync(ct);
    }

    public async Task<DateTimeOffset?> GetExpiryAsync(string userId, string sessionId, CancellationToken ct)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _)) return null;
        var value = await Tokens.AsNoTracking().Where(x => x.UserId == userId && x.LoginProvider == Provider && x.Name == sessionId)
            .Select(x => x.Value).SingleOrDefaultAsync(ct);
        return TryExpiry(value, out var expiry) && expiry > clock.GetUtcNow() ? expiry : null;
    }

    public async Task RevokeAsync(string userId, string sessionId, CancellationToken ct) =>
        _ = await Tokens.Where(x => x.UserId == userId && x.LoginProvider == Provider && x.Name == sessionId).ExecuteDeleteAsync(ct);

    private static bool TryExpiry(string? value, out DateTimeOffset expiry)
    {
        expiry = default;
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253402300799) return false;
        expiry = DateTimeOffset.FromUnixTimeSeconds(seconds); return true;
    }
}

public sealed class SingleSignOnSessionValidator(IOptions<SingleSignOnOptions> options, ISingleSignOnSessions sessions,
    IOptions<IdentityOptions> identity)
{
    public Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken ct) => !options.Value.Enabled
        ? Task.FromResult(true) : Validate(principal, ct);

    private async Task<bool> Validate(ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = principal.FindFirstValue(identity.Value.ClaimsIdentity.UserIdClaimType);
        var sessionId = principal.FindFirstValue(SingleSignOnOptions.SessionClaim);
        return !string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(sessionId)
            && await sessions.GetExpiryAsync(userId, sessionId, ct) is not null;
    }
}
