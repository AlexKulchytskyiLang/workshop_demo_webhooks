using System.Diagnostics.Metrics;

namespace InvoiceHub.Webhooks.Delivery;

/// <summary>The numbers the resilience proof reads (ASR-03 latency, ASR-05/07 throughput and back-pressure, ASR-13 observability).</summary>
internal static class WebhooksTelemetry
{
    public static readonly Meter Meter = new("InvoiceHub.Webhooks");

    public static readonly Counter<long> DeliveriesCreated = Meter.CreateCounter<long>("invoicehub.webhooks.deliveries.created", description: "Deliveries created by fan-out");
    public static readonly Counter<long> Attempts = Meter.CreateCounter<long>("invoicehub.webhooks.attempts", description: "Delivery attempts by outcome");
    public static readonly Counter<long> Deferred = Meter.CreateCounter<long>("invoicehub.webhooks.deferred", description: "Deliveries put back without an attempt (endpoint busy, circuit open)");
    public static readonly Counter<long> Swept = Meter.CreateCounter<long>("invoicehub.webhooks.swept", description: "Deliveries re-queued or reclaimed by the sweeper");
    public static readonly Counter<long> Terminal = Meter.CreateCounter<long>("invoicehub.webhooks.terminal", description: "Deliveries that ended as dead / held");
    public static readonly Counter<long> Warnings = Meter.CreateCounter<long>("invoicehub.webhooks.autodisable.warnings", description: "Auto-disable pre-warnings");

    /// <summary>Seconds from the business change committing to the first delivery attempt starting (ASR-03: p95 within 5 s).</summary>
    public static readonly Histogram<double> CommitToFirstAttempt = Meter.CreateHistogram<double>("invoicehub.webhooks.commit_to_first_attempt", unit: "s");

    public static readonly Histogram<double> AttemptDuration = Meter.CreateHistogram<double>("invoicehub.webhooks.attempt.duration", unit: "ms");
}
