using System.Security.Claims;

namespace InvoiceHub.BuildingBlocks.Security;

/// <summary>
/// Step-up authentication for dangerous actions (revealing a signing secret, ASR-09). The portal re-authenticates the person
/// (<c>prompt=login&amp;max_age=0</c>); the API accepts the action only if the token's <c>auth_time</c> is recent, so a stolen or idle session cannot do it.
/// </summary>
public static class StepUp
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static bool IsFresh(this ClaimsPrincipal user, DateTimeOffset now) =>
        long.TryParse(user.FindFirstValue("auth_time"), out var seconds)
        && now - DateTimeOffset.FromUnixTimeSeconds(seconds) <= MaxAge
        && now - DateTimeOffset.FromUnixTimeSeconds(seconds) >= TimeSpan.FromMinutes(-1);
}
