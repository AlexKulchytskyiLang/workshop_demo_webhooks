using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.BuildingBlocks.Security;

/// <summary>What an API key may do. A key carries a subset of these; each endpoint of the pull API asks for one.</summary>
public static class ApiScopes
{
    public const string EventsRead = "events:read";
    public const string KycRead = "kyc:read";

    public static readonly IReadOnlyList<string> All = [EventsRead, KycRead];

    public static string Describe(string scope) => scope switch
    {
        EventsRead => "Read the event stream, to reconcile what you received against what happened.",
        KycRead => "Read the result of an identity verification. This is personal data, so every read is audited.",
        _ => scope,
    };
}

/// <summary>Platform API keys (<c>ihk_...</c>) authenticate the pull API under their own scheme, next to the portal's Keycloak tokens.</summary>
public static class ApiKeyAuth
{
    public const string Scheme = "ApiKey";
    public const string ScopeClaim = "api_scope";
    public const string KeyIdClaim = "api_key_id";
    public const string Prefix = "ihk_";

    public static string PolicyFor(string scope) => $"apikey:{scope}";

    internal static AuthorizationBuilder AddApiKeyPolicies(this AuthorizationBuilder builder)
    {
        foreach (var scope in ApiScopes.All)
            builder.AddPolicy(PolicyFor(scope), p => p.AddAuthenticationSchemes(Scheme).RequireAuthenticatedUser().RequireClaim(ScopeClaim, scope));
        return builder;
    }

    /// <summary>Only an API key that carries this scope may call the endpoint (a portal login cannot).</summary>
    public static TBuilder RequireApiScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PolicyFor(scope));
}
