namespace InvoiceHub.Webhooks.Core;

/// <summary>What we remember about an endpoint's health. Stored on the endpoint row so every worker replica sees the same breaker.</summary>
public sealed record EndpointHealth(int ConsecutiveFailures, DateTimeOffset? FailingSince, DateTimeOffset? CircuitOpenUntil, int ProbeStep)
{
    public static readonly EndpointHealth Healthy = new(0, null, null, 0);

    public bool IsCircuitOpen(DateTimeOffset now) => CircuitOpenUntil is { } until && until > now;
}

/// <summary>Circuit breaker (ASR-07): 5 consecutive failures open it; probes at 30 s doubling to 15 min; the first success closes it.</summary>
public static class CircuitBreaker
{
    public const int FailureThreshold = 5;
    public const int MinThreshold = 2;
    public const int MaxThreshold = 50;
    public static readonly TimeSpan FirstProbe = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxProbe = TimeSpan.FromMinutes(15);

    public static EndpointHealth OnSuccess(EndpointHealth _) => EndpointHealth.Healthy;

    /// <param name="countsTowardBreaker">False for failures that do not mean the receiver is sick (429, 401...): they never open the circuit.</param>
    public static EndpointHealth OnFailure(EndpointHealth health, bool countsTowardBreaker, DateTimeOffset now, double probeScale = 1.0, int threshold = FailureThreshold)
    {
        if (!countsTowardBreaker) return health;
        var failures = health.ConsecutiveFailures + 1;
        var failingSince = health.FailingSince ?? now;
        if (failures < threshold) return health with { ConsecutiveFailures = failures, FailingSince = failingSince };

        // threshold reached (or a probe failed again): open for the next probe interval, doubling each time
        var step = health.CircuitOpenUntil is null ? 0 : health.ProbeStep + 1;
        var wait = TimeSpan.FromTicks((long)(Math.Min(FirstProbe.Ticks * Math.Pow(2, step), MaxProbe.Ticks) * probeScale));
        return new EndpointHealth(failures, failingSince, now + wait, step);
    }
}

public sealed record AutoDisableRule(int ConsecutiveFailures = 100, TimeSpan Within = default, TimeSpan ContinuousFor = default)
{
    public static readonly AutoDisableRule Default = new(100, TimeSpan.FromHours(24), TimeSpan.FromDays(5));

    public TimeSpan WithinWindow => Within == default ? TimeSpan.FromHours(24) : Within;

    public TimeSpan ContinuousWindow => ContinuousFor == default ? TimeSpan.FromDays(5) : ContinuousFor;
}

public enum AutoDisableDecision { None, Warn25, Warn50, Warn75, Disable }

public static class AutoDisable
{
    /// <summary>
    /// Rule (UX-SPEC D3): N consecutive failures within T hours, OR continuously failing for D days. The second rule exists because a quiet
    /// endpoint that gets 3 events a day would never reach 100. Pre-warnings at 25 / 50 / 75 % of N come first, so a person can act
    /// before anything is held (ASR-22).
    /// </summary>
    public static AutoDisableDecision Evaluate(AutoDisableRule rule, EndpointHealth health, DateTimeOffset now)
    {
        if (health.ConsecutiveFailures == 0 || health.FailingSince is null) return AutoDisableDecision.None;
        var failingFor = now - health.FailingSince.Value;

        if (failingFor >= rule.ContinuousWindow) return AutoDisableDecision.Disable;
        if (health.ConsecutiveFailures >= rule.ConsecutiveFailures && failingFor <= rule.WithinWindow) return AutoDisableDecision.Disable;

        // warnings fire exactly once, on the failure that reaches the threshold
        var n = health.ConsecutiveFailures;
        if (n == Threshold(rule, 75)) return AutoDisableDecision.Warn75;
        if (n == Threshold(rule, 50)) return AutoDisableDecision.Warn50;
        if (n == Threshold(rule, 25)) return AutoDisableDecision.Warn25;
        return AutoDisableDecision.None;
    }

    private static int Threshold(AutoDisableRule rule, int percent) => Math.Max(1, (int)Math.Ceiling(rule.ConsecutiveFailures * percent / 100.0));
}

/// <summary>The only legal moves of the delivery lifecycle. Everything else is a bug.</summary>
public static class DeliveryStateMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        ["pending"] = ["in_flight", "held", "dead"],
        ["in_flight"] = ["succeeded", "retry_scheduled", "held", "dead"],
        ["retry_scheduled"] = ["in_flight", "held", "dead"],
        ["held"] = ["pending", "dead"],
        ["dead"] = ["pending"],
        ["succeeded"] = ["pending"],
    };

    public static bool CanMove(string from, string to) => Allowed.TryGetValue(from, out var next) && next.Contains(to);
}
