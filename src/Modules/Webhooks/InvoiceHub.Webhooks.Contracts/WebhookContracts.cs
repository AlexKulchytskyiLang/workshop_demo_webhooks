namespace InvoiceHub.Webhooks.Contracts;

/// <summary>Delivery lifecycle (design/UX-SPEC.md D3, TECH-VISION 6.4).</summary>
public static class DeliveryStatus
{
    public const string Pending = "pending";
    public const string InFlight = "in_flight";
    public const string Succeeded = "succeeded";
    public const string RetryScheduled = "retry_scheduled";
    public const string Held = "held";
    public const string Dead = "dead";
}

/// <summary>Stored endpoint status. "Degraded" and "circuit open" are derived from counters, not stored.</summary>
public static class EndpointStatus
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Disabled = "disabled";
}
