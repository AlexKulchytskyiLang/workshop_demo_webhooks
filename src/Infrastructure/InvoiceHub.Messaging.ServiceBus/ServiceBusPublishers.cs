using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using InvoiceHub.BuildingBlocks.Messaging;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Messaging.ServiceBus;

internal static class MessagingTelemetry
{
    public static readonly ActivitySource Source = new("InvoiceHub.Messaging");

    public const string TraceParentProperty = "traceparent";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>
/// Publishes committed events to the <c>events</c> topic. <c>MessageId = event id</c> and the topic has duplicate detection, so the relay's
/// at-least-once republish after a crash collapses to one message (the fan-out's unique key is the second line of defence).
/// </summary>
internal sealed class ServiceBusEventTransport(ServiceBusClient client, IOptions<ServiceBusOptions> options) : IEventTransport, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.EventsTopic);

    public async Task PublishAsync(IReadOnlyList<EventEnvelope> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        using var activity = MessagingTelemetry.Source.StartActivity("events.publish", ActivityKind.Producer);
        activity?.SetTag("messaging.batch.message_count", batch.Count);

        var messages = batch.Select(e => new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(e, MessagingTelemetry.Json))
        {
            MessageId = e.EventId.ToString("N"),
            ContentType = "application/json",
            Subject = e.Type,
            ApplicationProperties =
            {
                ["tenant_id"] = e.TenantId.ToString(),
                ["type"] = e.Type,
                [MessagingTelemetry.TraceParentProperty] = e.TraceParent ?? activity?.Id,
            },
        }).ToList();

        await ServiceBusBatches.SendAsync(_sender, messages, ct);
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}

/// <summary>
/// The <c>deliveries</c> queue: one message per delivery, carrying ids only (state lives in SQL). A retry is a scheduled message with a fresh
/// MessageId. Scheduled messages cannot be cancelled (Spike 0), so the handler always re-reads SQL and ignores stale ones.
/// </summary>
internal sealed class ServiceBusDeliveryQueue(ServiceBusClient client, IOptions<ServiceBusOptions> options) : IDeliveryQueue, IAsyncDisposable
{
    private readonly ServiceBusSender _sender = client.CreateSender(options.Value.DeliveriesQueue);

    public async Task EnqueueAsync(IReadOnlyList<DeliveryMessage> messages, DateTimeOffset? scheduledFor, CancellationToken ct)
    {
        if (messages.Count == 0) return;
        using var activity = MessagingTelemetry.Source.StartActivity("deliveries.enqueue", ActivityKind.Producer);
        activity?.SetTag("messaging.batch.message_count", messages.Count);

        var sbMessages = messages.Select(m => new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(m, MessagingTelemetry.Json))
        {
            MessageId = $"{m.DeliveryId:N}-{m.Attempt.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}",
            ContentType = "application/json",
            ApplicationProperties =
            {
                ["tenant_id"] = m.TenantId.ToString(),
                [MessagingTelemetry.TraceParentProperty] = m.TraceParent ?? activity?.Id,
            },
        }).ToList();

        if (scheduledFor is { } at && at > DateTimeOffset.UtcNow.AddMilliseconds(200))
        {
            foreach (var chunk in sbMessages.Chunk(100)) await BrokerGate.RunAsync(() => _sender.ScheduleMessagesAsync(chunk, at, ct), ct);
        }
        else
        {
            await ServiceBusBatches.SendAsync(_sender, sbMessages, ct);
        }
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();
}

/// <summary>
/// Limits how many broker operations (sends, timers, settlements) one process has in flight at once. A burst of dozens of handlers all talking to the broker in the same
/// instant is what makes a broker push back (and the Service Bus client then pauses everything for ten seconds); a few at a time keeps the same throughput without the pause.
/// </summary>
internal static class BrokerGate
{
    private static readonly SemaphoreSlim Permits = new(4);

    public static async Task RunAsync(Func<Task> operation, CancellationToken ct)
    {
        await Permits.WaitAsync(ct);
        try { await operation(); }
        finally { Permits.Release(); }
    }
}

internal static class ServiceBusBatches
{
    /// <summary>Sends as many messages per AMQP batch as fit (the broker limits batch size), preserving order.</summary>
    public static async Task SendAsync(ServiceBusSender sender, IReadOnlyList<ServiceBusMessage> messages, CancellationToken ct)
    {
        var batch = await sender.CreateMessageBatchAsync(ct);
        try
        {
            foreach (var message in messages)
            {
                if (batch.TryAddMessage(message)) continue;
                if (batch.Count == 0) throw new InvalidOperationException("A single message is larger than the broker allows.");

                await BrokerGate.RunAsync(() => sender.SendMessagesAsync(batch, ct), ct);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(ct);
                if (!batch.TryAddMessage(message)) throw new InvalidOperationException("A single message is larger than the broker allows.");
            }
            if (batch.Count > 0) await BrokerGate.RunAsync(() => sender.SendMessagesAsync(batch, ct), ct);
        }
        finally
        {
            batch.Dispose();
        }
    }
}
