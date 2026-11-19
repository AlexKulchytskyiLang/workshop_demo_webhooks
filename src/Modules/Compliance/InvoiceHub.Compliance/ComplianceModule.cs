using System.Security.Claims;
using System.Text.Json;
using InvoiceHub.Audit.Contracts;
using InvoiceHub.BuildingBlocks.Data;
using InvoiceHub.BuildingBlocks.Http;
using InvoiceHub.BuildingBlocks.Modules;
using InvoiceHub.BuildingBlocks.Security;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Compliance.Contracts;
using InvoiceHub.Events.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace InvoiceHub.Compliance;

internal sealed class ComplianceDbContext(DbContextOptions<ComplianceDbContext> options) : DbContext(options);

/// <summary>
/// Identity verification. It shows the thin-event pattern end to end (ASR-12): the event says "verification X finished: approved" and NOTHING personal;
/// the receiver pulls the details with an API key that has the <c>kyc:read</c> scope, and every such read leaves an audit line.
/// </summary>
public sealed class ComplianceModule : IModule
{
    public const string ModuleName = "compliance";

    public string Name => ModuleName;

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddModuleDbContext<ComplianceDbContext>(builder.Configuration, builder.Environment.ApplicationName);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IKycCommands, KycCommands>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app) => app.MapGet("/v1/kyc/{id}", Get)
                                                              .RequireApiScope(ApiScopes.KycRead)
                                                              .WithTags("Pull API");

    /// <summary>The personal data of one verification. Not found covers "never existed" and "belongs to another tenant" alike (row-level security hides the row).</summary>
    private static async Task<IResult> Get(string id, ClaimsPrincipal caller, ComplianceDbContext db, IAuditLog audit, CancellationToken ct)
    {
        var raw = id.StartsWith("kyc_", StringComparison.Ordinal) ? id[4..] : id;
        if (!Guid.TryParse(raw, out var guid))
            return Problems.NotFound("verification_not_found", "No such verification.");

        var rows = await db.QueryAsync("SELECT decision, risk_score, subject_name, date_of_birth, document_number, country, completed_at FROM compliance.verification WHERE id = @id",
            r => new
            {
                decision = r.GetString(0), riskScore = r.GetInt32(1), name = r.GetString(2), dateOfBirth = DateOnly.FromDateTime(r.GetDateTime(3)), document = r.GetString(4), country = r.GetString(5),
                completedAt = new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc)),
            }, ct, ("@id", guid));

        if (rows.Count == 0)
            return Problems.NotFound("verification_not_found", "No such verification.");

        var v = rows[0];
        // no trace, no data: the line is written first, and failing to write it fails the request (ASR-12, ASR-19)
        await audit.WriteAsync(null, new AuditEntry("kyc.read", "verification", $"kyc_{guid:N}", null,
                               new { keyId = caller.FindFirstValue(ApiKeyAuth.KeyIdClaim), scope = ApiScopes.KycRead }), ct);

        return Results.Ok(new
        {
            verification_id = $"kyc_{guid:N}",
            decision = v.decision,
            risk_score = v.riskScore,
            completed_at = v.completedAt,
            subject = new { name = v.name, date_of_birth = v.dateOfBirth.ToString("yyyy-MM-dd"), document_number = v.document, country = v.country },
        });
    }

    private sealed class KycCommands(ComplianceDbContext db, IEventPublisher events, ITenantContext tenant, TimeProvider clock) : IKycCommands
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public async Task<KycRef> CompleteAsync(CompleteKyc c, CancellationToken ct = default)
        {
            var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Verifications belong to a tenant: set the tenant context first.");
            if (c.Decision is not ("approved" or "review" or "rejected"))
                throw new ArgumentException("The decision must be approved, review or rejected.", nameof(c));

            var id = Guid.CreateVersion7();
            var publicId = $"kyc_{id:N}";

            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
                await db.ExecuteAsync("""
                    INSERT compliance.verification (id, tenant_id, decision, risk_score, subject_name, date_of_birth, document_number, country)
                    VALUES (@id, @tenant, @decision, @risk, @name, @dob, @doc, @country)
                    """, ct, ("@id", id), ("@tenant", tenantId), ("@decision", c.Decision), ("@risk", c.RiskScore), ("@name", c.SubjectName), ("@dob", c.DateOfBirth.ToDateTime(TimeOnly.MinValue)),
                    ("@doc", c.DocumentNumber), ("@country", c.Country.ToUpperInvariant()));

                // the event: an id and a decision. Not a name, not a date of birth, not a document number.
                var data = JsonSerializer.Serialize(new { verification_id = publicId, decision = c.Decision }, Json);
                await events.PublishAsync(tx.GetDbTransaction(), new DomainEvent("kyc.completed", publicId, 1, data, clock.GetUtcNow()), ct);
                await tx.CommitAsync(ct);
            });

            return new KycRef(id, publicId, c.Decision);
        }
    }
}
