namespace InvoiceHub.BuildingBlocks.Messaging;

// The transport is replaceable (ASR-16): modules talk to these small interfaces, the Service Bus adapter implements them,
// and unit tests use in-memory fakes. Messages carry ids only: the state lives in SQL (principle P3).

/// <summary>"An event was committed": what the relay publishes and fan-out consumes.</summary>
public sealed record EventEnvelope(Guid EventId, Guid TenantId, string Type, DateTimeOffset OccurredAt, string? TraceParent = null);

/// <summary>"Attempt this delivery now".</summary>
public sealed record DeliveryMessage(Guid DeliveryId, Guid TenantId, int Attempt, string? TraceParent = null);

/// <summary>Publishes committed events to the event stream (Service Bus topic).</summary>
public interface IEventTransport
{
    Task PublishAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct);
}

/// <summary>Queue of delivery work. A scheduled message is the retry timer; the SQL row stays the source of truth.</summary>
public interface IDeliveryQueue
{
    /// <summary>Longest delay we trust the broker to hold a message (emulator TTL is 1 hour). Longer waits are left to the sweeper.</summary>
    static readonly TimeSpan MaxSchedulingHorizon = TimeSpan.FromMinutes(30);

    Task EnqueueAsync(IReadOnlyList<DeliveryMessage> messages, DateTimeOffset? scheduledFor, CancellationToken ct);
}

/// <summary>Handles one event for fan-out. Throwing means "try again later" (the broker redelivers).</summary>
public interface IEventMessageHandler
{
    Task HandleAsync(EventEnvelope message, CancellationToken ct);
}

/// <summary>Handles one delivery message. Throwing means "try again later"; <see cref="PoisonMessageException"/> means "never retry".</summary>
public interface IDeliveryMessageHandler
{
    Task HandleAsync(DeliveryMessage message, CancellationToken ct);
}

/// <summary>The message can never succeed (for example it names a delivery of another tenant): dead-letter it, do not retry.</summary>
public sealed class PoisonMessageException(string reason, string description) : Exception(description)
{
    public string Reason { get; } = reason;
}
