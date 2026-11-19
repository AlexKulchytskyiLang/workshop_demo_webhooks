using System.Data.Common;

namespace InvoiceHub.Events.Contracts;

/// <summary>How an event's data travels to a subscriber (see the payload design slide).</summary>
public static class PayloadStyle
{
    public const string Fat = "fat";
    public const string Thin = "thin";
    public const string ClaimCheck = "claim";
}

/// <summary>A business fact to publish. The tenant is the caller's tenant context; the event id becomes the stable <c>webhook-id</c>.</summary>
/// <param name="Type">A catalogue event type such as <c>invoice.generated</c>.</param>
/// <param name="AggregateId">The business object (for example the invoice id). Ordering is per aggregate.</param>
/// <param name="Sequence">Per-aggregate version: receivers apply an event only if it is newer than what they hold.</param>
/// <param name="DataJson">The canonical event data as a JSON object. Never put personal data in events of a PII class.</param>
public sealed record DomainEvent(
    string Type,
    string AggregateId,
    long Sequence,
    string DataJson,
    DateTimeOffset OccurredAt,
    bool IsTest = false,
    Guid? Id = null);

public sealed record StoredEvent(
    Guid Id,
    Guid TenantId,
    string Type,
    string AggregateId,
    long Sequence,
    string DataJson,
    DateTimeOffset OccurredAt,
    bool IsTest);

/// <summary>
/// The transactional outbox (principle P2): the event and its outbox row are written in the SAME transaction as the business
/// change. The caller passes its own open transaction; nothing is sent from here.
/// </summary>
public interface IEventPublisher
{
    Task<Guid> PublishAsync(DbTransaction transaction, DomainEvent domainEvent, CancellationToken ct = default);
}

/// <summary>
/// Fan-out reports back here once an event has become deliveries. Events that are published but never reported are republished by the relay,
/// so a message lost inside the broker (the emulator keeps nothing on restart) or one that kept failing can never mean a lost event.
/// </summary>
public interface IFanOutTracker
{
    Task MarkFannedOutAsync(Guid eventId, CancellationToken ct = default);
}

/// <summary>Reads committed events under the current tenant context (workers set it per message).</summary>
public interface IEventReader
{
    Task<StoredEvent?> GetAsync(Guid eventId, CancellationToken ct = default);

    /// <summary>The newest events of one type for the current tenant (at most 100), used to test a subscription filter against real data.</summary>
    Task<IReadOnlyList<StoredEvent>> RecentAsync(string type, int limit, CancellationToken ct = default);
}

public sealed record EventTypeInfo(string Type, string Description, string Payload, bool ContainsPii, string Category, IReadOnlyList<string> ReferenceKeys);

public sealed record CatalogCategory(string Name, bool Pii, IReadOnlyList<EventTypeInfo> Events);

public sealed record CatalogTemplate(string? Name, string? Description, IReadOnlyList<string> Events);

public interface IEventCatalog
{
    IReadOnlyList<CatalogCategory> Categories { get; }

    IReadOnlyDictionary<string, IReadOnlyList<string>> Bundles { get; }

    IReadOnlyDictionary<string, CatalogTemplate> Templates { get; }

    EventTypeInfo? Find(string type);

    bool Exists(string type);
}
