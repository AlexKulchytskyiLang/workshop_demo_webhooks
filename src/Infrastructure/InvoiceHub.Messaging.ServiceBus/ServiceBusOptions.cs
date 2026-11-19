using System.ComponentModel.DataAnnotations;

namespace InvoiceHub.Messaging.ServiceBus;

public sealed class ServiceBusOptions
{
    public const string Section = "ServiceBus";
    public const string ConnectionName = "servicebus";

    public string EventsTopic { get; set; } = "events";

    public string FanOutSubscription { get; set; } = "fanout";

    public string DeliveriesQueue { get; set; } = "deliveries";

    /// <summary>Parallel fan-out handlers per process. Each one is a cheap SQL round trip.</summary>
    [Range(1, 64)]
    public int FanOutConcurrency { get; set; } = 8;

    /// <summary>Parallel delivery attempts per process (replicas multiply this). The per-endpoint slots still cap what one receiver sees.</summary>
    [Range(1, 128)]
    public int DeliveryConcurrency { get; set; } = 16;

    [Range(0, 500)]
    public int PrefetchCount { get; set; } = 32;

    /// <summary>On shutdown, in-flight handlers get this long to finish before they are cancelled (their deliveries are then handed back, not lost).</summary>
    [Range(1, 120)]
    public int DrainSeconds { get; set; } = 25;
}
