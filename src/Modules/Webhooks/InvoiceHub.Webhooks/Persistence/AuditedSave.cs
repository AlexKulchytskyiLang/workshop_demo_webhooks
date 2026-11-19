using InvoiceHub.Audit.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InvoiceHub.Webhooks.Persistence;

internal static class AuditedSave
{
    /// <summary>
    /// Saves the pending changes and writes the audit line in ONE transaction (ASR-19): either both are stored or neither is, so there is never a change
    /// without its trace and never a trace of a change that did not happen. <paramref name="within"/> runs extra statements in the same transaction.
    /// </summary>
    public static Task SaveAsync(this WebhooksDbContext db, IAuditLog audit, AuditEntry entry, CancellationToken ct, Func<IDbContextTransaction, Task>? within = null) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct);
            if (within is not null) await within(tx);
            await audit.WriteAsync(tx.GetDbTransaction(), entry, ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.AcceptAllChanges();
        });
}
