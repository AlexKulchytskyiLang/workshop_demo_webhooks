using System.Data.Common;

namespace InvoiceHub.Audit.Contracts;

/// <summary>
/// One line of the audit log. Never put secrets or personal data in <see cref="Details"/>: it is stored as written and shown to administrators.
/// The actor, their role, the caller's IP address and the trace id are filled in from the current request; background work passes <see cref="Actor"/> itself.
/// </summary>
/// <param name="Action">What happened, as <c>area.verb</c> (for example <c>endpoint.updated</c>, <c>secret.revealed</c>).</param>
/// <param name="Details">Anything that explains the change (field names and new values that are safe to show). Serialised to JSON.</param>
public sealed record AuditEntry(
    string Action, string TargetType, string? TargetId, string? TargetName = null, object? Details = null, string? Actor = null, string? ActorRole = null);

public sealed record AuditRecord(
    long Id, DateTimeOffset At, string Actor, string? ActorRole, string Action, string TargetType, string? TargetId, string? TargetName, string? DetailsJson, string? Ip, string? TraceId);

/// <summary>
/// Writes audit lines. Passing the caller's open transaction makes the audit row commit or roll back together with the change it describes
/// (ASR-19: no change without a trace, no trace without a change). The log is append-only: the application logins cannot update or delete it.
/// </summary>
public interface IAuditLog
{
    Task WriteAsync(DbTransaction? transaction, AuditEntry entry, CancellationToken ct = default);
}
