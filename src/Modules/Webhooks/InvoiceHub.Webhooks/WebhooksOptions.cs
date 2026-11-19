using System.ComponentModel.DataAnnotations;

namespace InvoiceHub.Webhooks;

public sealed class WebhooksOptions
{
    public const string Section = "Webhooks";

    public DeliveryOptions Delivery { get; set; } = new();

    public EgressOptions Egress { get; set; } = new();

    public SweeperOptions Sweeper { get; set; } = new();

    public ReplayOptions Replay { get; set; } = new();
}

public sealed class ReplayOptions
{
    /// <summary>Events one tenant may re-send per rolling 24 hours (UX-SPEC D9). A replay is a deliberate act, so it is capped like one.</summary>
    [Range(1, 1_000_000)]
    public int DailyCap { get; set; } = 5000;

    /// <summary>How fast a replay job releases deliveries when the person does not choose (events per second).</summary>
    [Range(1, 1000)]
    public int DefaultRatePerSecond { get; set; } = 5;

    /// <summary>"Resume" needs a passing test no older than this.</summary>
    [Range(1, 1440)]
    public int TestFreshMinutes { get; set; } = 30;

    /// <summary>"Only recent": how far back the events to deliver may reach.</summary>
    [Range(1, 720)]
    public int RecentHours { get; set; } = 24;
}

public sealed class DeliveryOptions
{
    /// <summary>Largest slice of a receiver's reply we keep (ASR-14 / general NFR: response cap).</summary>
    [Range(256, 16384)]
    public int ResponseCapBytes { get; set; } = 4096;

    /// <summary>When a worker finds every per-endpoint slot busy it re-queues the delivery after about this long instead of waiting.</summary>
    [Range(100, 60_000)]
    public int BusyRetryMilliseconds { get; set; } = 1500;

    /// <summary>Held deliveries released by "resume" are spread out by this much each (rate-limited release, UX-SPEC D3).</summary>
    [Range(0, 10_000)]
    public int ResumeSpacingMilliseconds { get; set; } = 200;

    /// <summary>Scales the circuit breaker's probe waits. 1 = production values (30 s doubling to 15 min); tests use a small fraction.</summary>
    [Range(0.001, 1.0)]
    public double ProbeScale { get; set; } = 1.0;

    /// <summary>Forces every endpoint onto this retry preset (e.g. "demo" so a workshop can watch retries in seconds). Empty = each endpoint's own preset.</summary>
    public string? ForceRetryPreset { get; set; }

    /// <summary>How long the download link of a claim-check event stays valid. It is minted again for every attempt, so a retry hours later gets a fresh one.</summary>
    [Range(1, 1440)]
    public int ClaimLinkMinutes { get; set; } = 15;
}

public sealed class EgressOptions
{
    /// <summary>
    /// "host:port" entries a webhook may target even though they are loopback/HTTP (the demo receivers). Honoured ONLY when the host
    /// environment is Development; in any other environment the list is ignored and the guard stays strict.
    /// </summary>
    public List<string> DevAllow { get; set; } = [];
}

public sealed class SweeperOptions
{
    [Range(100, 60_000)]
    public int PollMilliseconds { get; set; } = 1000;

    [Range(1, 1000)]
    public int BatchSize { get; set; } = 200;

    /// <summary>How long the sweeper believes a message it just queued is alive, before it would queue another.</summary>
    [Range(5, 3600)]
    public int DispatchLeaseSeconds { get; set; } = 60;
}
