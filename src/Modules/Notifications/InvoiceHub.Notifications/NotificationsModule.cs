using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Messaging;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Notifications;

internal sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options);

internal sealed class NotificationsOptions
{
    public const string Section = "Notifications";

    /// <summary>SMTP server for email alerts (Mailpit in the demo). Empty = email alerts are skipped with a warning, in-app alerts still work.</summary>
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 1025;

    [Required]
    public string From { get; set; } = "InvoiceHub <alerts@invoicehub.test>";

    /// <summary>Where the links in emails point.</summary>
    [Required]
    public string PortalBaseUrl { get; set; } = "http://localhost:5173";
}

/// <summary>What can raise an alert, and who is told by default (UX-SPEC D10). Every trigger comes from a platform event, so alerts use the same pipeline as everything else.</summary>
internal sealed record AlertTrigger(string Name, string EventType, string Label, string Description, bool InApp, bool Email, string[] Roles);

internal static class AlertTriggers
{
    public static readonly IReadOnlyList<AlertTrigger> All =
    [
        new("degraded", "webhook.endpoint.degraded", "Endpoint is failing",
            "An endpoint reached 25%, 50% or 75% of the failures that would switch it off automatically. Time to act, before anything is held.",
            InApp: true, Email: false, Roles: [Roles.Owner, Roles.Admin, Roles.Developer]),
        new("auto_disabled", "webhook.endpoint.auto_disabled", "Endpoint switched off",
            "An endpoint was switched off (too many failures, or the receiver said it is gone). New events are held, not lost.",
            InApp: true, Email: true, Roles: [Roles.Owner, Roles.Admin, Roles.Developer]),
    ];

    public static AlertTrigger? ByEvent(string eventType) => All.FirstOrDefault(t => t.EventType == eventType);

    public static AlertTrigger? ByName(string name) => All.FirstOrDefault(t => t.Name == name);

    public static string Pack(IEnumerable<string> roles) => "," + string.Join(',', roles) + ",";

    public static string[] Unpack(string packed) => packed.Split(',', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Alert rules, the in-app inbox (the bell) and email alerts. It only listens to platform events; it never reads another module's tables.</summary>
public sealed class NotificationsModule : IWorkerModule
{
    public const string ModuleName = "notifications";
    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddOptions<NotificationsOptions>().Bind(builder.Configuration.GetSection(NotificationsOptions.Section)).ValidateDataAnnotations();
        builder.Services.AddModuleDbContext<NotificationsDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
    }

    public void ConfigureWorkers(IHostApplicationBuilder builder) => builder.Services.AddScoped<IEventMessageHandler, AlertHandler>();

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/portal").WithTags("Notifications");
        g.MapGet("/notifications", Inbox).RequireAuthorization();
        g.MapPost("/notifications/read-all", ReadAll).RequireAuthorization();
        g.MapPost("/notifications/{id:long}/read", MarkRead).RequireAuthorization();
        g.MapGet("/alerts", Rules).RequireAuthorization(Permissions.EndpointsRead);
        g.MapPut("/alerts/{trigger}", SetRule).RequireAuthorization(Permissions.AlertsManage);
    }

    // ------------------------------------------------------------------ the bell

    private static async Task<IResult> Inbox(ClaimsPrincipal user, NotificationsDbContext db, int? limit, CancellationToken ct)
    {
        var roles = user.TenantRoles();
        var sub = user.UserId();

        if (roles.Count == 0 || sub is null)
            return Results.Ok(new { items = Array.Empty<object>(), unread = 0 });

        var (filter, parameters) = RoleFilter(roles);

        var args = parameters.Append(("@me", (object?)sub.Value)).Append(("@take", (object?)Math.Clamp(limit ?? 30, 1, 100))).ToArray();
        var items = await db.QueryAsync($"""
            SELECT TOP (@take) i.id, i.at, i.severity, i.title, i.body, i.endpoint_id, i.kind, CASE WHEN r.item_id IS NULL THEN 0 ELSE 1 END
            FROM notifications.inbox_item i LEFT JOIN notifications.inbox_read r ON r.item_id = i.id AND r.user_sub = @me
            WHERE i.visible = 1 AND ({filter})
            ORDER BY i.id DESC
            """,
            r => new { id = r.GetInt64(0), at = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), severity = r.GetString(2), title = r.GetString(3), body = r.GetString(4),
                endpointId = r.IsDBNull(5) ? (Guid?)null : r.GetGuid(5), kind = r.GetString(6), read = r.GetInt32(7) == 1 }, ct, args);
        var unread = await db.ScalarAsync<int>($"""
            SELECT COUNT(*) FROM notifications.inbox_item i
            WHERE i.visible = 1 AND ({filter}) AND NOT EXISTS (SELECT 1 FROM notifications.inbox_read r WHERE r.item_id = i.id AND r.user_sub = @me)
            """, ct, args);

        return Results.Ok(new { items, unread });
    }

    private static async Task<IResult> MarkRead(long id, ClaimsPrincipal user, ITenantContext tenant, NotificationsDbContext db, CancellationToken ct)
    {
        if (user.UserId() is not { } sub) return Results.NoContent();
        await db.ExecuteAsync("""
            INSERT notifications.inbox_read (tenant_id, item_id, user_sub)
            SELECT @tenant, i.id, @me FROM notifications.inbox_item i
            WHERE i.id = @id AND NOT EXISTS (SELECT 1 FROM notifications.inbox_read r WHERE r.item_id = i.id AND r.user_sub = @me)
            """, ct, ("@tenant", tenant.TenantId), ("@me", sub), ("@id", id));
        return Results.NoContent();
    }

    private static async Task<IResult> ReadAll(ClaimsPrincipal user, ITenantContext tenant, NotificationsDbContext db, CancellationToken ct)
    {
        var roles = user.TenantRoles();
        if (user.UserId() is not { } sub || roles.Count == 0) return Results.NoContent();
        var (filter, parameters) = RoleFilter(roles);
        await db.ExecuteAsync($"""
            INSERT notifications.inbox_read (tenant_id, item_id, user_sub)
            SELECT @tenant, i.id, @me FROM notifications.inbox_item i
            WHERE i.visible = 1 AND ({filter}) AND NOT EXISTS (SELECT 1 FROM notifications.inbox_read r WHERE r.item_id = i.id AND r.user_sub = @me)
            """, ct, parameters.Append(("@tenant", (object?)tenant.TenantId)).Append(("@me", (object?)sub)).ToArray());
        return Results.NoContent();
    }

    /// <summary>An alert is for the roles it was addressed to: "for_roles" holds ",owner,admin,", so a role matches when ",role," is inside it.</summary>
    private static (string Filter, (string, object?)[] Parameters) RoleFilter(IReadOnlyList<string> roles) =>
        (string.Join(" OR ", roles.Select((_, i) => $"i.for_roles LIKE @r{i}")), roles.Select((r, i) => ($"@r{i}", (object?)$"%,{r},%")).ToArray());

    // ------------------------------------------------------------------ rules

    private static async Task<IResult> Rules(NotificationsDbContext db, CancellationToken ct)
    {
        var stored = await db.QueryAsync("SELECT trigger_name, enabled, in_app, email, recipient_roles, updated_at, updated_by FROM notifications.alert_rule",
            r => (Name: r.GetString(0), Enabled: r.GetBoolean(1), InApp: r.GetBoolean(2), Email: r.GetBoolean(3), Roles: AlertTriggers.Unpack(r.GetString(4)), At: r.GetDateTime(5), By: r.IsDBNull(6) ? null : r.GetString(6)), ct);

        return Results.Ok(new
        {
            items = AlertTriggers.All.Select(t =>
            {
                var s = stored.FirstOrDefault(x => x.Name == t.Name);
                var customised = s != default;
                return new
                {
                    trigger = t.Name, label = t.Label, description = t.Description,
                    enabled = customised ? s.Enabled : true, inApp = customised ? s.InApp : t.InApp, email = customised ? s.Email : t.Email, roles = customised ? s.Roles : t.Roles,
                    isDefault = !customised, updatedBy = s.By, updatedAt = customised ? new DateTimeOffset(DateTime.SpecifyKind(s.At, DateTimeKind.Utc)) : (DateTimeOffset?)null,
                };
            }),
            roles = Roles.All,
        });
    }

    internal sealed record RuleInput(bool? Enabled, bool? InApp, bool? Email, string[]? Roles);

    private static async Task<IResult> SetRule(string trigger, RuleInput input, ClaimsPrincipal user, ITenantContext tenant, NotificationsDbContext db, IAuditLog audit, CancellationToken ct)
    {
        var t = AlertTriggers.ByName(trigger);
        if (t is null)
            return Problems.NotFound("unknown_trigger", $"Unknown alert '{trigger}'.");

        var roles = (input.Roles ?? t.Roles).Distinct().ToArray();
        if (roles.Any(r => !Roles.All.Contains(r)))
            return Problems.Validation("invalid_role", $"Roles: {string.Join(", ", Roles.All)}.");

        var enabled = input.Enabled ?? true;
        var inApp = input.InApp ?? t.InApp;
        var email = input.Email ?? t.Email;
        if (enabled && roles.Length == 0)
            return Problems.Validation("no_recipients", "Choose at least one role to notify, or switch the alert off.");
        if (enabled && !inApp && !email)
            return Problems.Validation("no_channel", "Choose at least one way to notify: in the portal, by email, or both.");

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.ExecuteAsync("""
                MERGE notifications.alert_rule AS r USING (SELECT @tenant AS tenant_id, @trigger AS trigger_name) AS s ON r.tenant_id = s.tenant_id AND r.trigger_name = s.trigger_name
                WHEN MATCHED THEN UPDATE SET enabled = @enabled, in_app = @inApp, email = @email, recipient_roles = @roles, updated_at = SYSUTCDATETIME(), updated_by = @by
                WHEN NOT MATCHED THEN INSERT (tenant_id, trigger_name, enabled, in_app, email, recipient_roles, updated_by) VALUES (@tenant, @trigger, @enabled, @inApp, @email, @roles, @by);
                """, ct, ("@tenant", tenant.TenantId), ("@trigger", trigger), ("@enabled", enabled), ("@inApp", inApp), ("@email", email), ("@roles", AlertTriggers.Pack(roles)),
                ("@by", user.FindFirstValue("preferred_username")));
            await audit.WriteAsync(tx.GetDbTransaction(), new AuditEntry("alerts.changed", "alert", trigger, t.Label, new { enabled, inApp, email, roles }), ct);
            await tx.CommitAsync(ct);
        });

        return Results.NoContent();
    }

    // ------------------------------------------------------------------ the handler

    /// <summary>
    /// Turns a platform event about an endpoint into an alert: an inbox item (once per event, thanks to the unique key) and, if the rule says so, an email to everyone
    /// in the chosen roles. An email that cannot be sent is logged and does not fail the event: the in-app alert is the record, and the message must not be retried forever.
    /// </summary>
    internal sealed class AlertHandler(IEventReader events, ITenantScope scope, ITeamDirectory team,
                                       NotificationsDbContext db, IEmailSender email,
                                       IOptions<NotificationsOptions> options, ILogger<AlertHandler> logger) : IEventMessageHandler
    {
        public async Task HandleAsync(EventEnvelope message, CancellationToken ct)
        {
            var trigger = AlertTriggers.ByEvent(message.Type);
            if (trigger is null)
                return;

            scope.SetTenant(message.TenantId);
            var ev = await events.GetAsync(message.EventId, ct);
            if (ev is null || ev.IsTest)
                return;

            var stored = await db.QueryAsync("SELECT enabled, in_app, email, recipient_roles FROM notifications.alert_rule WHERE trigger_name = @t",
                r => (Enabled: r.GetBoolean(0), InApp: r.GetBoolean(1), Email: r.GetBoolean(2), Roles: AlertTriggers.Unpack(r.GetString(3))), ct, ("@t", trigger.Name));
            var (enabled, inApp, wantsEmail, roles) = stored.Count > 0 ? stored[0] : (true, trigger.InApp, trigger.Email, trigger.Roles);
            if (!enabled)
                return;

            var text = Compose(trigger, ev.DataJson, options.Value.PortalBaseUrl);
            var inserted = await db.ExecuteAsync("""
                INSERT notifications.inbox_item (tenant_id, event_id, kind, severity, title, body, endpoint_id, for_roles, visible)
                SELECT @tenant, @event, @kind, @severity, @title, @body, @endpoint, @roles, @visible
                WHERE NOT EXISTS (SELECT 1 FROM notifications.inbox_item WHERE tenant_id = @tenant AND event_id = @event AND kind = @kind)
                """, ct, ("@tenant", message.TenantId), ("@event", message.EventId), ("@kind", trigger.Name), ("@severity", text.Severity), ("@title", text.Title), ("@body", text.Body),
                ("@endpoint", text.EndpointId), ("@roles", AlertTriggers.Pack(roles)), ("@visible", inApp));

            if (inserted == 0 || !wantsEmail)
                return; // a redelivered message: the first delivery already told everyone

            await SendEmailsAsync(roles, text, ct);
        }

        private async Task SendEmailsAsync(string[] roles, AlertText text, CancellationToken ct)
        {
            var recipients = await team.ListActiveAsync(roles, ct);
            foreach (var member in recipients)
            {
                try
                {
                    await email.SendAsync(member.Email, text.Title, text.Body + "\n\n" + text.Link + "\n\n— InvoiceHub", ct);
                }
                catch (Exception e) when (e is SmtpException or InvalidOperationException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning(e, "Could not email the alert “{Title}” to {Recipient}", text.Title, member.Email);
                }
            }
        }
    }

    internal sealed record AlertText(string Severity, string Title, string Body, Guid? EndpointId, string Link);

    internal static AlertText Compose(AlertTrigger trigger, string dataJson, string portalBase)
    {
        using var doc = JsonDocument.Parse(dataJson);
        var d = doc.RootElement;
        string Str(string name, string fallback = "") => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        int Int(string name) => d.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

        var name = Str("name", "Your endpoint");
        Guid? endpoint = Guid.TryParseExact(Str("endpoint_id").Replace("ep_", ""), "N", out var id) ? id : null;
        var link = endpoint is null ? portalBase : $"{portalBase.TrimEnd('/')}/endpoints/{endpoint:D}/logs";
        var failures = Int("consecutive_failures");

        if (trigger.Name == "degraded")
        {
            var percent = Int("level_percent");
            var limit = Int("auto_disable_at_failures");
            return new AlertText(percent >= 75 ? "critical" : "warning",
                $"“{name}” is failing: {percent}% of the way to being switched off",
                $"{failures} deliveries in a row have failed. At {limit} failures in a row the endpoint is switched off automatically and new events are held. Look at the errors in the delivery log and fix the problem, or pause the endpoint yourself.",
                endpoint, link);
        }

        var reason = Str("reason");
        return reason == "gone"
            ? new AlertText("critical", $"“{name}” was switched off: your server said it is gone (410)",
                "Your server answered 410 Gone, which means the address no longer exists. New events are held, not lost. Update the URL, send a test and resume the endpoint in the Replay center.", endpoint, $"{portalBase.TrimEnd('/')}/replay")
            : new AlertText("critical", $"“{name}” was switched off automatically",
                $"It failed {failures} times in a row. New events are held, not lost. Fix the problem, send a test and resume the endpoint in the Replay center, where you choose what happens to the held events.", endpoint, $"{portalBase.TrimEnd('/')}/replay");
    }
}

internal interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>Plain SMTP, enough for the demo (Mailpit catches everything). A real deployment swaps this one class for its mail provider.</summary>
internal sealed class SmtpEmailSender(IOptions<NotificationsOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost))
        {
            logger.LogWarning("No SMTP server is configured (Notifications:SmtpHost): the email “{Subject}” to {Recipient} was not sent", subject, to);
            return;
        }
        using var client = new SmtpClient(o.SmtpHost, o.SmtpPort) { EnableSsl = false, DeliveryMethod = SmtpDeliveryMethod.Network, Timeout = 8000, Credentials = CredentialCache.DefaultNetworkCredentials };
        using var mail = new MailMessage(new MailAddress(ParseFrom(o.From).Address, ParseFrom(o.From).DisplayName), new MailAddress(to)) { Subject = subject, Body = body, IsBodyHtml = false };
        await client.SendMailAsync(mail, ct);
    }

    private static MailAddress ParseFrom(string from) => new(from);
}
