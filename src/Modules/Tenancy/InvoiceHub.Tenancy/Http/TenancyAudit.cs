using InvoiceHub.Audit.Contracts;
using InvoiceHub.Tenancy.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceHub.Tenancy.Http;

internal static class TenancyAudit
{
    /// <summary>Saves the pending changes and the audit line in one transaction (ASR-19): both are stored or neither is.</summary>
    public static Task SaveAsync(TenancyDbContext db, IAuditLog audit, AuditEntry entry, CancellationToken ct) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
            await audit.WriteAsync(tx.GetDbTransaction(), entry, ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.AcceptAllChanges();
        });
}
