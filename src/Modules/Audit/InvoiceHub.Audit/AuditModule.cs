using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.Audit;

internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options);

/// <summary>Who did what, to what, from where. Append-only by database permission, tenant-isolated by row-level security.</summary>
public sealed class AuditModule : IModule
{
    public const string ModuleName = "audit";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddModuleDbContext<AuditDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        builder.Services.AddScoped<IAuditLog, SqlAuditLog>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app) =>
        app.MapGet("/api/portal/audit", Query)
           .RequireAuthorization(Permissions.AuditView)
           .WithTags("Audit");

    private static async Task<IResult> Query(AuditDbContext db, string? action, string? actor, string? targetType, string? targetId, DateTime? from, DateTime? to, string? cursor, int? limit, CancellationToken ct)
    {
        from = from.AsUtc();
        to = to.AsUtc();
        var take = Math.Clamp(limit ?? 50, 1, 200);
        long? before = null;
        if (cursor is not null)
        {
            if (!long.TryParse(cursor, out var parsed)) return Problems.Validation("invalid_cursor", "The cursor is not valid.");
            before = parsed;
        }

        var rows = await db.QueryAsync("""
            SELECT TOP (@take) id, at, actor, actor_role, action, target_type, target_id, target_name, details_json, ip, trace_id
            FROM audit.entry
            WHERE (@before IS NULL OR id < @before)
              AND (@action IS NULL OR action = @action OR action LIKE @actionPrefix)
              AND (@actor IS NULL OR actor = @actor)
              AND (@targetType IS NULL OR target_type = @targetType)
              AND (@targetId IS NULL OR target_id = @targetId)
              AND (@from IS NULL OR at >= @from) AND (@to IS NULL OR at < @to)
            ORDER BY id DESC
            """,
            r => new AuditRecord(r.GetInt64(0), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)),
            ct, ("@take", take + 1), ("@before", before), ("@action", action), ("@actionPrefix", action is null ? null : action.TrimEnd('*') + "%"), ("@actor", actor), ("@targetType", targetType),
            ("@targetId", targetId), ("@from", from), ("@to", to));

        var page = rows.Take(take).ToList();
        return Results.Ok(new
        {
            items = page.Select(r => new
            {
                r.Id, r.At, r.Actor, r.ActorRole, r.Action, r.TargetType, r.TargetId, r.TargetName, r.Ip, r.TraceId,
                details = r.DetailsJson is null ? (JsonElement?)null : JsonDocument.Parse(r.DetailsJson).RootElement.Clone(),
            }),
            nextCursor = rows.Count > take ? page[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        });
    }

    private sealed class SqlAuditLog(AuditDbContext db, ITenantContext tenant, IHttpContextAccessor http) : IAuditLog
    {
        private const string Insert = """
            INSERT audit.entry (tenant_id, actor, actor_role, action, target_type, target_id, target_name, details_json, ip, trace_id)
            VALUES (@tenant, @actor, @role, @action, @type, @id, @name, @details, @ip, @trace)
            """;

        public async Task WriteAsync(DbTransaction? transaction, AuditEntry e, CancellationToken ct = default)
        {
            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Audit lines belong to a tenant: set the tenant context first.");
            var context = http.HttpContext;
            var actor = e.Actor ?? (context?.User.Identity?.IsAuthenticated == true ? context.User.FindFirst("preferred_username")?.Value ?? context.User.FindFirst("sub")?.Value : null) ?? "system";
            var role = e.ActorRole ?? (context?.User.Identity?.IsAuthenticated == true ? RolePermissions.Primary(context.User.TenantRoles()) : null);
            var parameters = new (string, object?)[]
            {
                ("@tenant", tenantId), ("@actor", Trim(actor, 80)), ("@role", role), ("@action", Trim(e.Action, 60)), ("@type", Trim(e.TargetType, 30)), ("@id", Trim(e.TargetId, 64)),
                ("@name", Trim(e.TargetName, 120)), ("@details", e.Details is null ? null : JsonSerializer.Serialize(e.Details, Json)), ("@ip", Trim(context?.Connection.RemoteIpAddress?.ToString(), 45)),
                ("@trace", Activity.Current?.TraceId.ToString()),
            };

            if (transaction is null)
            {
                await db.ExecuteAsync(Insert, ct, parameters);
                return;
            }

            // the caller's own transaction: the line commits or rolls back with the change
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = Insert;
            foreach (var (name, value) in parameters)
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                command.Parameters.Add(p);
            }
            await command.ExecuteNonQueryAsync(ct);
        }

        private static string? Trim(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
    }
}
