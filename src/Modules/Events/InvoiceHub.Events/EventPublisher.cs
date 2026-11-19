using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using InvoiceHub.BuildingBlocks.Tenancy;
using InvoiceHub.Events.Contracts;
using InvoiceHub.Events.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvoiceHub.Events;

internal static class EventsTelemetry
{
    public static readonly ActivitySource Source = new("InvoiceHub.Events");
    public static readonly Meter Meter = new("InvoiceHub.Events");
    public static readonly Counter<long> Published = Meter.CreateCounter<long>("invoicehub.events.published", description: "Events committed to the outbox");
    public static readonly Counter<long> Republished = Meter.CreateCounter<long>("invoicehub.events.republished", description: "Events published again because fan-out never confirmed them");
    public static readonly Counter<long> Relayed = Meter.CreateCounter<long>("invoicehub.events.relayed", description: "Outbox rows handed to the broker");
}

/// <summary>Writes the event and its outbox row inside the caller's transaction (no dual write, no distributed transaction).</summary>
internal sealed class EventPublisher(ITenantContext tenant, IEventCatalog catalog) : IEventPublisher
{
    public async Task<Guid> PublishAsync(DbTransaction transaction, DomainEvent e, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Events are published for a tenant: set the tenant context first.");
        if (!catalog.Exists(e.Type)) throw new ArgumentException($"Unknown event type '{e.Type}'.", nameof(e));
        using (JsonDocument.Parse(e.DataJson)) { } // reject malformed JSON before it reaches the database

        var id = e.Id ?? Guid.CreateVersion7();
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT events.event (id, tenant_id, type, aggregate_id, sequence, data_json, occurred_at, is_test, trace_parent)
            VALUES (@id, @tenant, @type, @aggregate, @sequence, @data, @at, @test, @trace);
            INSERT events.outbox (event_id, tenant_id) VALUES (@id, @tenant);
            """;
        Add(command, "@id", id);
        Add(command, "@tenant", tenantId);
        Add(command, "@type", e.Type);
        Add(command, "@aggregate", e.AggregateId);
        Add(command, "@sequence", e.Sequence);
        Add(command, "@data", e.DataJson);
        Add(command, "@at", e.OccurredAt.UtcDateTime);
        Add(command, "@test", e.IsTest);
        Add(command, "@trace", (object?)Activity.Current?.Id ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);

        EventsTelemetry.Published.Add(1, new KeyValuePair<string, object?>("type", e.Type));
        return id;
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}

internal sealed class EventReader(EventsDbContext db) : IEventReader
{
    public async Task<IReadOnlyList<StoredEvent>> RecentAsync(string type, int limit, CancellationToken ct = default) =>
        await db.Events.AsNoTracking().Where(x => x.Type == type && !x.IsTest)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => new StoredEvent(x.Id, x.TenantId, x.Type, x.AggregateId, x.Sequence, x.DataJson,
                new DateTimeOffset(DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc)), x.IsTest))
            .ToListAsync(ct);

    public async Task<StoredEvent?> GetAsync(Guid eventId, CancellationToken ct = default) =>
        await db.Events.AsNoTracking().Where(x => x.Id == eventId)
            .Select(x => new StoredEvent(x.Id, x.TenantId, x.Type, x.AggregateId, x.Sequence, x.DataJson,
                new DateTimeOffset(DateTime.SpecifyKind(x.OccurredAt, DateTimeKind.Utc)), x.IsTest))
            .FirstOrDefaultAsync(ct);
}

internal sealed class FanOutTracker(EventsDbContext db) : IFanOutTracker
{
    public Task MarkFannedOutAsync(Guid eventId, CancellationToken ct = default) =>
        db.Outbox.Where(r => r.EventId == eventId && r.FannedOutAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.FannedOutAt, DateTime.UtcNow), ct);
}
