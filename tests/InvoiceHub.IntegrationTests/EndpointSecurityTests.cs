using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.IntegrationTests;

/// <summary>
/// "Secure by default, fail closed" (P5): every route in the API either requires authorization or is on an explicit allow-list
/// of anonymous routes. A new endpoint that forgets <c>RequireAuthorization()</c> fails this test.
/// Runs the real host in-process (no containers): nothing here touches SQL or Keycloak.
/// </summary>
public class EndpointSecurityTests : IClassFixture<EndpointSecurityTests.ApiFactory>
{
    public sealed class ApiFactory : WebApplicationFactory<InvoiceHub.Api.SecurityHeadersMiddleware>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Keycloak:Authority", "https://localhost:8085/realms/invoicehub");
            builder.UseSetting("ConnectionStrings:invoicehub", "Server=localhost;Database=none;User Id=x;Password=x;TrustServerCertificate=true");
            builder.UseSetting("Db:Api:User", "api_user");
            builder.UseSetting("Db:Api:Password", "unused");
            builder.UseSetting("Secrets:MasterKey", "test-only-master-key-0123456789-abcdefghij");
        }
    }

    /// <summary>Routes that are public on purpose. Adding to this list is a security decision: justify it in the pull request.</summary>
    private static readonly HashSet<string> AnonymousAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/public/tenants",
        "POST /api/public/registrations",
        "* /health/ready",
        "* /health/live",
        "GET /openapi/{documentName}.json",
        "GET /api/public/endpoints/{id:guid}/jwks.json", // the PUBLIC half of an endpoint's ES256 keys: receivers must fetch it without signing in. Rate limited, cacheable, no secrets.
    };

    private readonly ApiFactory _factory;

    public EndpointSecurityTests(ApiFactory factory) => _factory = factory;

    private IEnumerable<(string Route, RouteEndpoint Endpoint)> Routes() =>
        _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(ds => ds.Endpoints.OfType<RouteEndpoint>())
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => ($"{m} {e.RoutePattern.RawText}", e)));

    [Fact]
    public void Every_route_requires_authorization_unless_it_is_on_the_anonymous_allow_list()
    {
        var open = Routes()
            .Where(r => r.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && r.Endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(r => r.Route).ToList();
        Assert.True(open.Count == 0, "Routes with no authorization metadata: " + string.Join(", ", open));
    }

    [Fact]
    public void The_anonymous_routes_are_exactly_the_documented_ones()
    {
        var anonymous = Routes().Where(r => r.Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null && !r.Route.StartsWith("OPTIONS", StringComparison.Ordinal))
            .Select(r => r.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Subset(AnonymousAllowList, anonymous);
    }
}
