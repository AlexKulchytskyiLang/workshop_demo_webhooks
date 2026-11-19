using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Notifications;
using InvoiceHub.Tenancy.ApiKeys;
using InvoiceHub.Tenancy.Contracts;
using InvoiceHub.Tenancy.Http;
using InvoiceHub.Webhooks.Core;
using InvoiceHub.Webhooks.Persistence;
using InvoiceHub.Webhooks.Sending;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace InvoiceHub.UnitTests;

/// <summary>The pure rules behind the M4 screens: API keys, header masking in the log, alert wording, temporary passwords and the disabled-account check.</summary>
public class PortalAdvancedTests
{
    // ------------------------------------------------------------------ API keys

    [Fact]
    public void An_api_key_carries_its_tenant_and_id_and_round_trips()
    {
        var tenant = Guid.NewGuid();
        var id = Guid.NewGuid();
        var (key, prefix, hash) = ApiKeyFormat.Create(tenant, id);

        Assert.StartsWith("ihk_", key, StringComparison.Ordinal);
        Assert.StartsWith("ihk_" + id.ToString("N")[..8], prefix, StringComparison.Ordinal);
        var parsed = ApiKeyFormat.TryParse(key);
        Assert.NotNull(parsed);
        Assert.Equal(tenant, parsed.TenantId);
        Assert.Equal(id, parsed.KeyId);
        Assert.Equal(hash, ApiKeyFormat.Hash(parsed.Secret));
    }

    [Fact]
    public void Two_keys_never_share_a_secret_and_the_secret_is_not_in_the_prefix()
    {
        var a = ApiKeyFormat.Create(Guid.NewGuid(), Guid.NewGuid());
        var b = ApiKeyFormat.Create(Guid.NewGuid(), Guid.NewGuid());
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.DoesNotContain(ApiKeyFormat.TryParse(a.Key)!.Secret, a.Prefix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ihk_")]
    [InlineData("abc_123")]
    [InlineData("ihk_notaguid_notaguid_secret")]
    [InlineData("ihk_00000000000000000000000000000001_00000000000000000000000000000002_short")]
    public void A_malformed_key_is_rejected_before_any_lookup(string token) => Assert.Null(ApiKeyFormat.TryParse(token));

    [Fact]
    public void A_tampered_secret_does_not_match_the_stored_hash()
    {
        var (key, _, hash) = ApiKeyFormat.Create(Guid.NewGuid(), Guid.NewGuid());
        var parsed = ApiKeyFormat.TryParse(key)!;
        var forged = parsed.Secret[..^1] + (parsed.Secret[^1] == 'A' ? 'B' : 'A');
        Assert.NotEqual(hash, ApiKeyFormat.Hash(forged));
    }

    // ------------------------------------------------------------------ roles and temporary passwords

    [Fact]
    public void A_temporary_password_has_every_character_class_and_is_never_repeated()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 50; i++)
        {
            var p = TeamApi.TemporaryPassword();
            Assert.Equal(16, p.Length);
            Assert.Contains(p, c => char.IsUpper(c));
            Assert.Contains(p, c => char.IsLower(c));
            Assert.Contains(p, c => char.IsDigit(c));
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.True(seen.Add(p));
        }
    }

    [Fact]
    public void Only_owners_and_admins_manage_roles_and_only_they_read_the_audit_log()
    {
        foreach (var role in new[] { Roles.Developer, Roles.Support, Roles.Viewer })
        {
            Assert.DoesNotContain(Permissions.RolesManage, RolePermissions.For([role]));
            Assert.DoesNotContain(Permissions.AuditView, RolePermissions.For([role]));
        }
        Assert.Contains(Permissions.RolesManage, RolePermissions.For([Roles.Admin]));
        Assert.Contains(Permissions.AuditView, RolePermissions.For([Roles.Owner]));
    }

    [Fact]
    public void Support_may_replay_one_event_but_not_run_a_replay_job()
    {
        var support = RolePermissions.For([Roles.Support]);
        Assert.Contains(Permissions.ReplaySingle, support);
        Assert.DoesNotContain(Permissions.ReplayRun, support);
        Assert.Contains(Permissions.PayloadsView, support);
        Assert.DoesNotContain(Permissions.PayloadsView, RolePermissions.For([Roles.Viewer]));
    }

    // ------------------------------------------------------------------ what the delivery log may show of a request

    private static EndpointEntity Endpoint() => new()
    {
        AuthType = "header",
        AuthHeaderName = "X-Api-Key",
        HeadersJson = """[{"name":"X-Region","value":"eu","secret":false},{"name":"X-Customer-Token","value":"ZW5j","secret":true}]""",
    };

    [Fact]
    public void The_log_never_stores_a_credential_or_a_secret_header()
    {
        var shown = HeaderMasking.Apply(Endpoint(), new Dictionary<string, string>
        {
            ["X-Api-Key"] = "super-secret-key",
            ["X-Customer-Token"] = "plain-token-value",
            ["X-Region"] = "eu",
            ["Authorization"] = "Bearer abc",
            ["webhook-id"] = "evt_123",
        });

        Assert.Equal(HeaderMasking.Mask, shown["X-Api-Key"]);
        Assert.Equal(HeaderMasking.Mask, shown["X-Customer-Token"]);
        Assert.Equal(HeaderMasking.Mask, shown["Authorization"]);
        Assert.Equal("eu", shown["X-Region"]);
        Assert.Equal("evt_123", shown["webhook-id"]);
    }

    [Fact]
    public void The_signature_is_shortened_but_the_key_id_header_stays_readable()
    {
        var shown = HeaderMasking.Apply(Endpoint(), new Dictionary<string, string>
        {
            [WebhookSigning.SignatureHeader] = "v1,AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA= v1a,BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            [SignatureHeaderBuilder.KeyIdHeader] = "k_abc123",
        });

        Assert.All(shown[WebhookSigning.SignatureHeader].Split(' '), part => Assert.True(part.Length <= 17));
        Assert.EndsWith("...", shown[WebhookSigning.SignatureHeader].Split(' ')[0], StringComparison.Ordinal);
        Assert.Equal("k_abc123", shown[SignatureHeaderBuilder.KeyIdHeader]);
    }

    // ------------------------------------------------------------------ alert wording

    private static string Data(object o) => JsonSerializer.Serialize(o);

    [Fact]
    public void A_pre_warning_names_the_endpoint_the_progress_and_what_happens_next()
    {
        var endpoint = Guid.NewGuid();
        var text = NotificationsModule.Compose(AlertTriggers.ByName("degraded")!,
            Data(new { endpoint_id = $"ep_{endpoint:N}", name = "Contoso ERP", level_percent = 50, consecutive_failures = 50, auto_disable_at_failures = 100 }), "https://portal.test");

        Assert.Equal("warning", text.Severity);
        Assert.Contains("Contoso ERP", text.Title, StringComparison.Ordinal);
        Assert.Contains("50%", text.Title, StringComparison.Ordinal);
        Assert.Contains("50 deliveries in a row", text.Body, StringComparison.Ordinal);
        Assert.Contains("100", text.Body, StringComparison.Ordinal);
        Assert.Equal(endpoint, text.EndpointId);
        Assert.Equal($"https://portal.test/endpoints/{endpoint:D}/logs", text.Link);
    }

    [Fact]
    public void The_last_warning_is_critical()
    {
        var text = NotificationsModule.Compose(AlertTriggers.ByName("degraded")!, Data(new { endpoint_id = "ep_x", name = "E", level_percent = 75, consecutive_failures = 75, auto_disable_at_failures = 100 }), "https://p.test");
        Assert.Equal("critical", text.Severity);
    }

    [Theory]
    [InlineData("auto_disabled", "switched off automatically", "failed 100 times")]
    [InlineData("gone", "your server said it is gone", "410 Gone")]
    public void A_switch_off_says_why_and_that_nothing_is_lost(string reason, string title, string body)
    {
        var text = NotificationsModule.Compose(AlertTriggers.ByName("auto_disabled")!, Data(new { endpoint_id = $"ep_{Guid.NewGuid():N}", name = "Contoso ERP", reason, consecutive_failures = 100 }), "https://p.test");

        Assert.Equal("critical", text.Severity);
        Assert.Contains(title, text.Title, StringComparison.Ordinal);
        Assert.Contains(body, text.Body, StringComparison.Ordinal);
        Assert.Contains("held, not lost", text.Body, StringComparison.Ordinal);
        Assert.EndsWith("/replay", text.Link, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_recipients_survive_the_round_trip_through_storage()
    {
        string[] roles = [Roles.Owner, Roles.Developer];
        Assert.Equal(roles, AlertTriggers.Unpack(AlertTriggers.Pack(roles)));
        Assert.Contains(",owner,", AlertTriggers.Pack(roles), StringComparison.Ordinal); // the inbox query matches ",role," inside it
    }

    // ------------------------------------------------------------------ disabling a member takes effect at once

    private sealed class Disabled(Guid who) : IDisabledUsers
    {
        public Task<bool> IsDisabledAsync(Guid keycloakSub, CancellationToken ct) => Task.FromResult(keycloakSub == who);
    }

    private sealed class OneTenant(TenantInfo tenant) : ITenantDirectory
    {
        public Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<TenantInfo?>(id == tenant.Id ? tenant : null);
        public Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken ct = default) => Task.FromResult<TenantInfo?>(slug == tenant.Slug ? tenant : null);
        public Task<IReadOnlyList<TenantSummary>> ListActiveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TenantSummary>>([]);
    }

    [Fact]
    public async Task A_disabled_member_is_stopped_even_though_their_token_is_still_valid()
    {
        var tenant = new TenantInfo(Guid.NewGuid(), "acme", "Acme", TenantStatus.Active);
        var sub = Guid.NewGuid();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(Auth.TenantClaim, tenant.Id.ToString()), new Claim("sub", sub.ToString())], "test"));
        var services = new ServiceCollection().AddLogging().AddProblemDetails().AddSingleton<IDisabledUsers>(new Disabled(sub)).BuildServiceProvider();
        var context = new DefaultHttpContext { User = user, RequestServices = services };
        context.Response.Body = new MemoryStream();
        var called = false;

        await new TenantResolutionMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(context, new OneTenant(tenant), new TenantContext());

        Assert.False(called);
        Assert.Equal(403, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        Assert.Contains("account_disabled", await new StreamReader(context.Response.Body).ReadToEndAsync(), StringComparison.Ordinal);
    }
}
