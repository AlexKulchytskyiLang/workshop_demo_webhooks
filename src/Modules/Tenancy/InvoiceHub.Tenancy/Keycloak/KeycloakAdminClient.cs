using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Security;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Tenancy.Keycloak;

internal sealed record NewKeycloakUser(string Email, string FirstName, string LastName, string Password, Guid TenantId, string TenantSlug, bool TemporaryPassword = false);

internal sealed class KeycloakConflictException(string message) : Exception(message);

internal sealed class KeycloakUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

internal interface IKeycloakAdmin
{
    /// <summary>Creates the user with the tenant attributes and returns its Keycloak id.</summary>
    Task<Guid> CreateUserAsync(NewKeycloakUser user, CancellationToken ct);

    Task AssignRealmRoleAsync(Guid userId, string role, CancellationToken ct);

    Task DeleteUserAsync(Guid userId, CancellationToken ct);

    /// <summary>Gives the user exactly this one tenant role: any other tenant role is taken away.</summary>
    Task ReplaceTenantRoleAsync(Guid userId, string role, CancellationToken ct);

    /// <summary>A disabled user cannot sign in; their existing sessions are ended.</summary>
    Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct);
}

/// <summary>
/// Keycloak Admin REST client authenticated with the <c>tenancy-admin</c> service account (client credentials).
/// The access token is cached until shortly before it expires. Used only by the registration use case.
/// </summary>
internal sealed class KeycloakAdminClient(IHttpClientFactory factory,
                                          IOptions<KeycloakOptions> options,
                                          TimeProvider clock) : IKeycloakAdmin, IDisposable
{
    /// <summary>Named HttpClient; registered without automatic retries for unsafe methods (a retried POST could create a user twice).</summary>
    public const string ClientName = "keycloak-admin";

    private HttpClient Http => factory.CreateClient(ClientName);

    public void Dispose() => _tokenLock.Dispose();

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    private string Realm => new Uri(options.Value.Authority).Segments.Last().Trim('/');

    private Uri Base => new(new Uri(options.Value.Authority).GetLeftPart(UriPartial.Authority));

    public async Task<Guid> CreateUserAsync(NewKeycloakUser user, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Post, $"/admin/realms/{Realm}/users", ct);
        request.Content = JsonContent.Create(new
        {
            username = user.Email,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            enabled = true,
            emailVerified = true, // demo: a production registration verifies the address by email first
            attributes = new Dictionary<string, string[]>
            {
                [Auth.TenantClaim] = [user.TenantId.ToString()],
                ["tenant_slug"] = [user.TenantSlug],
            },
            credentials = new[] { new { type = "password", value = user.Password, temporary = user.TemporaryPassword } },
        });

        using var response = await SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new KeycloakConflictException("A user with this email already exists.");

        await EnsureSuccessAsync(response, "create user", ct);

        var location = response.Headers.Location?.Segments.LastOrDefault()?.Trim('/');
        if (!Guid.TryParse(location, out var id))
            throw new KeycloakUnavailableException("Keycloak did not return the new user's id.");
        return id;
    }

    public async Task AssignRealmRoleAsync(Guid userId, string role, CancellationToken ct)
    {
        using var get = await AuthorizedAsync(HttpMethod.Get, $"/admin/realms/{Realm}/roles/{Uri.EscapeDataString(role)}", ct);
        using var roleResponse = await SendAsync(get, ct);
        await EnsureSuccessAsync(roleResponse, "read role", ct);
        var representation = await roleResponse.Content.ReadFromJsonAsync<JsonElement>(ct);

        using var map = await AuthorizedAsync(HttpMethod.Post, $"/admin/realms/{Realm}/users/{userId}/role-mappings/realm", ct);
        map.Content = JsonContent.Create(new[] { representation });
        using var mapResponse = await SendAsync(map, ct);
        await EnsureSuccessAsync(mapResponse, "assign role", ct);
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken ct)
    {
        using var request = await AuthorizedAsync(HttpMethod.Delete, $"/admin/realms/{Realm}/users/{userId}", ct);
        using var response = await SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, "delete user", ct);
    }

    public async Task ReplaceTenantRoleAsync(Guid userId, string role, CancellationToken ct)
    {
        using var get = await AuthorizedAsync(HttpMethod.Get, $"/admin/realms/{Realm}/users/{userId}/role-mappings/realm", ct);
        using var current = await SendAsync(get, ct);
        await EnsureSuccessAsync(current, "read user roles", ct);
        var mapped = (await current.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray().ToList();

        var stale = mapped.Where(r => r.GetProperty("name").GetString() is { } n && n != role && Roles.All.Contains(n)).ToList();
        if (stale.Count != 0)
        {
            using var remove = await AuthorizedAsync(HttpMethod.Delete, $"/admin/realms/{Realm}/users/{userId}/role-mappings/realm", ct);
            remove.Content = JsonContent.Create(stale);
            using var removed = await SendAsync(remove, ct);
            await EnsureSuccessAsync(removed, "remove roles", ct);
        }

        if (!mapped.Any(r => r.GetProperty("name").GetString() == role))
            await AssignRealmRoleAsync(userId, role, ct);
    }

    public async Task SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        using var update = await AuthorizedAsync(HttpMethod.Put, $"/admin/realms/{Realm}/users/{userId}", ct);
        update.Content = JsonContent.Create(new { enabled });
        using var response = await SendAsync(update, ct);
        await EnsureSuccessAsync(response, "update user", ct);

        if (enabled)
            return;
        // end the sessions they have open now, otherwise a disabled user keeps working until their token expires
        using var logout = await AuthorizedAsync(HttpMethod.Post, $"/admin/realms/{Realm}/users/{userId}/logout", ct);
        using var loggedOut = await SendAsync(logout, ct);
        await EnsureSuccessAsync(loggedOut, "end sessions", ct);
    }

    private async Task<HttpRequestMessage> AuthorizedAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, new Uri(Base, path));
        request.Headers.Authorization = new("Bearer", await GetTokenAsync(ct));
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await Http.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new KeycloakUnavailableException("Keycloak could not be reached.", e);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new KeycloakUnavailableException($"Keycloak {action} failed with {(int)response.StatusCode}: {body}");
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && clock.GetUtcNow() < _tokenExpires)
            return _token;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is not null && clock.GetUtcNow() < _tokenExpires)
                return _token;

            var kc = options.Value;
            using var response = await Http.PostAsync(
                new Uri(Base, $"/realms/{Realm}/protocol/openid-connect/token"),
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = kc.AdminClientId,
                    ["client_secret"] = kc.AdminClientSecret,
                }), ct);

            await EnsureSuccessAsync(response, "obtain admin token", ct);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            _token = json.GetProperty("access_token").GetString()!;
            _tokenExpires = clock.GetUtcNow().AddSeconds(json.GetProperty("expires_in").GetInt32() - 30);
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
