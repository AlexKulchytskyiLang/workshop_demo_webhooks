using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InvoiceHub.BuildingBlocks.Security;

public sealed class KeycloakOptions
{
    public const string Section = "Keycloak";

    /// <summary>The realm URL exactly as it appears in the token's <c>iss</c> claim, e.g. https://localhost:8085/realms/invoicehub.</summary>
    [Required, Url]
    public string Authority { get; set; } = "";

    [Required]
    public string Audience { get; set; } = "invoicehub-api";

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Confidential client with a service account, used by the Tenancy module to create users (registration).</summary>
    public string AdminClientId { get; set; } = "tenancy-admin";

    public string AdminClientSecret { get; set; } = "";
}

public static class Auth
{
    public const string TenantClaim = "tenant_id";
    public const string RolesClaim = "roles";

    public static IServiceCollection AddKeycloakAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KeycloakOptions>()
            .Bind(configuration.GetSection(KeycloakOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<KeycloakOptions>>((o, keycloak) =>
            {
                var kc = keycloak.Value;
                o.Authority = kc.Authority;
                o.RequireHttpsMetadata = kc.RequireHttpsMetadata;
                o.MapInboundClaims = false; // keep Keycloak's claim names (sub, roles, tenant_id)
                o.TokenValidationParameters = new()
                {
                    ValidIssuer = kc.Authority,
                    ValidAudience = kc.Audience,
                    NameClaimType = "preferred_username",
                    RoleClaimType = RolesClaim,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

        services.AddAuthorizationBuilder().AddPermissionPolicies().AddApiKeyPolicies();
        return services;
    }

    /// <summary>One authorization policy per permission, named like the permission (e.g. "endpoints:write").</summary>
    private static Microsoft.AspNetCore.Authorization.AuthorizationBuilder AddPermissionPolicies(
        this Microsoft.AspNetCore.Authorization.AuthorizationBuilder builder)
    {
        foreach (var permission in Permissions.All)
        {
            builder.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ctx.User.HasPermission(permission)));
        }
        return builder;
    }

    public static Guid? TenantId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TenantClaim), out var id) ? id : null;

    public static Guid? UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    public static IReadOnlyList<string> TenantRoles(this ClaimsPrincipal user) =>
        user.FindAll(RolesClaim).Select(c => c.Value).Where(Roles.All.Contains).Distinct().ToArray();

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        RolePermissions.For(user.TenantRoles()).Contains(permission);

    public static string DisplayName(this ClaimsPrincipal user) =>
        user.FindFirstValue("name") ?? user.FindFirstValue("preferred_username") ?? user.FindFirstValue("email") ?? "unknown";
}
