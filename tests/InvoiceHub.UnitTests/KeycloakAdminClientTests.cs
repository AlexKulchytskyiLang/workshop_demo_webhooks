using System.Net;
using System.Text;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.Tenancy.Keycloak;
using Microsoft.Extensions.Options;

namespace InvoiceHub.UnitTests;

public class KeycloakAdminClientTests
{
    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static KeycloakAdminClient Client(ScriptedHandler handler) => new(
        new Factory(handler),
        Options.Create(new KeycloakOptions { Authority = "https://localhost:8085/realms/invoicehub", AdminClientSecret = "s3cret" }),
        TimeProvider.System);

    private static HttpResponseMessage Default(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/realms/invoicehub/protocol/openid-connect/token" => Json(HttpStatusCode.OK, """{"access_token":"tok","expires_in":300}"""),
        "/admin/realms/invoicehub/users" when r.Method == HttpMethod.Post =>
            new HttpResponseMessage(HttpStatusCode.Created) { Headers = { Location = new Uri("https://localhost:8085/admin/realms/invoicehub/users/11111111-1111-1111-1111-111111111111") } },
        "/admin/realms/invoicehub/roles/owner" => Json(HttpStatusCode.OK, """{"id":"r1","name":"owner"}"""),
        var p when p.EndsWith("/role-mappings/realm", StringComparison.Ordinal) => new HttpResponseMessage(HttpStatusCode.NoContent),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Creating_a_user_posts_the_tenant_attributes_and_returns_the_new_id()
    {
        var handler = new ScriptedHandler(Default);
        var tenant = Guid.NewGuid();
        var id = await Client(handler).CreateUserAsync(new NewKeycloakUser("a@b.test", "Ann", "Bee", "Str0ng!Passw0rd", tenant, "acme"), default);

        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), id);
        var create = handler.Calls.Single(c => c.Path == "/admin/realms/invoicehub/users");
        Assert.Contains(tenant.ToString(), create.Body);
        Assert.Contains("\"tenant_slug\":[\"acme\"]", create.Body);
        Assert.Contains("\"temporary\":false", create.Body);
    }

    [Fact]
    public async Task The_admin_token_is_fetched_once_and_reused()
    {
        var handler = new ScriptedHandler(Default);
        var client = Client(handler);
        var user = new NewKeycloakUser("a@b.test", "Ann", "Bee", "Str0ng!Passw0rd", Guid.NewGuid(), "acme");
        await client.CreateUserAsync(user, default);
        await client.CreateUserAsync(user, default);
        Assert.Single(handler.Calls, c => c.Path.EndsWith("/openid-connect/token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_duplicate_user_is_reported_as_a_conflict_not_as_an_outage()
    {
        var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal) && r.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Conflict) : Default(r));
        await Assert.ThrowsAsync<KeycloakConflictException>(() =>
            Client(handler).CreateUserAsync(new NewKeycloakUser("a@b.test", "Ann", "Bee", "Str0ng!Passw0rd", Guid.NewGuid(), "acme"), default));
    }

    [Fact]
    public async Task Keycloak_errors_surface_as_unavailable()
    {
        var handler = new ScriptedHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/users", StringComparison.Ordinal)
            ? Json(HttpStatusCode.InternalServerError, "boom") : Default(r));
        await Assert.ThrowsAsync<KeycloakUnavailableException>(() =>
            Client(handler).CreateUserAsync(new NewKeycloakUser("a@b.test", "Ann", "Bee", "Str0ng!Passw0rd", Guid.NewGuid(), "acme"), default));
    }

    [Fact]
    public async Task Assigning_a_role_reads_the_role_then_maps_it_to_the_user()
    {
        var handler = new ScriptedHandler(Default);
        var user = Guid.NewGuid();
        await Client(handler).AssignRealmRoleAsync(user, "owner", default);

        Assert.Contains(handler.Calls, c => c.Method == HttpMethod.Get && c.Path == "/admin/realms/invoicehub/roles/owner");
        var map = handler.Calls.Single(c => c.Path == $"/admin/realms/invoicehub/users/{user}/role-mappings/realm");
        Assert.Contains("\"name\":\"owner\"", map.Body);
    }

    [Fact]
    public async Task Deleting_a_user_that_is_already_gone_is_not_an_error()
    {
        var handler = new ScriptedHandler(r => r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NotFound) : Default(r));
        await Client(handler).DeleteUserAsync(Guid.NewGuid(), default);
    }
}
