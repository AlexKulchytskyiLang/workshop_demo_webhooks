namespace InvoiceHub.BuildingBlocks.Security;

/// <summary>Tenant roles (Keycloak realm roles). Order matters: the first role a user holds in this list is their primary role.</summary>
public static class Roles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Developer = "developer";
    public const string Support = "support";
    public const string Viewer = "viewer";

    public static readonly IReadOnlyList<string> All = [Owner, Admin, Developer, Support, Viewer];
}

/// <summary>Capabilities from the portal's role matrix (design/UX-SPEC.md section 9). Each one is an authorization policy of the same name.</summary>
public static class Permissions
{
    public const string EndpointsRead = "endpoints:read";
    public const string EndpointsWrite = "endpoints:write";
    public const string EndpointsDelete = "endpoints:delete";
    public const string SecretsReveal = "secrets:reveal";
    public const string PayloadsView = "payloads:view";
    public const string ReplayRun = "replay:run";
    public const string ReplaySingle = "replay:single";
    public const string AlertsManage = "alerts:manage";
    public const string RolesManage = "roles:manage";
    public const string AuditView = "audit:view";
    public const string ApiKeysManage = "apikeys:manage";

    public static readonly IReadOnlyList<string> All =
    [
        EndpointsRead, EndpointsWrite, EndpointsDelete, SecretsReveal, PayloadsView, ReplayRun, ReplaySingle,
        AlertsManage, RolesManage, AuditView, ApiKeysManage,
    ];
}

public static class RolePermissions
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        [Roles.Owner] = [.. Permissions.All],
        [Roles.Admin] = [.. Permissions.All],
        [Roles.Developer] =
        [
            Permissions.EndpointsRead, Permissions.EndpointsWrite, Permissions.SecretsReveal, Permissions.PayloadsView,
            Permissions.ReplayRun, Permissions.ReplaySingle, Permissions.ApiKeysManage,
        ],
        [Roles.Support] = [Permissions.EndpointsRead, Permissions.PayloadsView, Permissions.ReplaySingle],
        [Roles.Viewer] = [Permissions.EndpointsRead],
    };

    public static IReadOnlyCollection<string> For(IEnumerable<string> roles) =>
        roles.Where(Map.ContainsKey).SelectMany(r => Map[r]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>The highest role the user holds (owner first), or null if they hold no tenant role.</summary>
    public static string? Primary(IEnumerable<string> roles)
    {
        var set = roles.ToHashSet(StringComparer.Ordinal);
        return Roles.All.FirstOrDefault(set.Contains);
    }
}
