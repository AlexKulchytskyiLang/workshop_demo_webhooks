using System.Globalization;
using System.Text.Json;

namespace InvoiceHub.Webhooks.Core;

/// <summary>One line of the schedule shown to a person: when each attempt happens.</summary>
public sealed record ScheduledAttempt(int Number, TimeSpan Delay, TimeSpan At);

/// <summary>
/// Retry schedule (ADR-07): 5 s, 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 8 h, 12 h, then every 12 h until the window closes,
/// each with +-20 % jitter so a recovering receiver is not hit by a synchronised wave. <c>Retry-After</c> is honoured as a floor.
/// A policy can also cap the number of attempts (the "custom" and "aggressive" presets do).
/// </summary>
public sealed record RetryPolicy(IReadOnlyList<TimeSpan> Delays, TimeSpan Window, double Jitter = 0.2, int? MaxAttempts = null)
{
    public static readonly RetryPolicy Standard = new(
        [
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(8), TimeSpan.FromHours(12),
        ],
        TimeSpan.FromHours(72));

    /// <summary>Fast recovery for receivers that are either up or clearly down: 8 attempts, first retry after 1 s, tripling up to 15 min, give up after 1 h.</summary>
    public static readonly RetryPolicy Aggressive = new RetryParameters(8, "exp3", 1, 900, 3600).ToPolicy();

    /// <summary>Demo preset: the same shape compressed so a workshop can watch a retry sequence in a minute or two.</summary>
    public static readonly RetryPolicy Demo = new(
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)],
        TimeSpan.FromMinutes(10));

    /// <summary>Automated tests: sub-second delays so a whole retry sequence runs in a few seconds.</summary>
    public static readonly RetryPolicy Test = new(
        [TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(2400)],
        TimeSpan.FromMinutes(3));

    public static RetryPolicy FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "demo" => Demo,
        "test" => Test,
        "aggressive" => Aggressive,
        _ => Standard,
    };

    /// <summary>The policy an endpoint has chosen: a named preset, or "custom" with its own parameters (an unreadable custom setting falls back to standard, never to "no retries").</summary>
    public static RetryPolicy For(string? preset, string? customJson)
    {
        if (!string.Equals(preset, "custom", StringComparison.OrdinalIgnoreCase)) return FromName(preset);
        return RetryParameters.TryParse(customJson, out var parameters) && !parameters!.Validate().Any() ? parameters.ToPolicy() : Standard;
    }

    /// <summary>Whether another attempt may follow attempt number <paramref name="attemptsMade"/>.</summary>
    public bool HasMoreAttempts(int attemptsMade) => MaxAttempts is not { } max || attemptsMade < max;

    /// <summary>The delay before attempt number <paramref name="attemptsMade"/> + 1 (1 = first retry), jittered.</summary>
    public TimeSpan NextDelay(int attemptsMade, TimeSpan? retryAfter, Random random)
    {
        var index = Math.Clamp(attemptsMade - 1, 0, Delays.Count - 1);
        var baseDelay = Delays[index];
        var jitter = 1 + ((random.NextDouble() * 2) - 1) * Jitter;
        var delay = TimeSpan.FromTicks((long)(baseDelay.Ticks * jitter));
        return retryAfter is { } floor && floor > delay ? floor : delay;
    }

    /// <summary>The time the n-th retry would be scheduled at without jitter.</summary>
    public IReadOnlyList<TimeSpan> Preview(int count) => PreviewAttempts(count + 1).Skip(1).Select(a => a.At).ToList();

    /// <summary>
    /// What the portal shows next to the settings ("Delivery &amp; resilience"): every attempt with its delay and its time since the event, without jitter.
    /// Stops at the attempt limit or when the next attempt would fall outside the window, exactly as delivery does.
    /// </summary>
    public IReadOnlyList<ScheduledAttempt> PreviewAttempts(int limit = 60)
    {
        var attempts = new List<ScheduledAttempt> { new(1, TimeSpan.Zero, TimeSpan.Zero) };
        var total = TimeSpan.Zero;
        while (attempts.Count < limit && HasMoreAttempts(attempts.Count))
        {
            var delay = Delays[Math.Clamp(attempts.Count - 1, 0, Delays.Count - 1)];
            if (total + delay > Window) break;
            total += delay;
            attempts.Add(new ScheduledAttempt(attempts.Count + 1, delay, total));
        }
        return attempts;
    }
}

/// <summary>The "custom" retry settings a tenant can edit. Validated before they are saved (a window shorter than the first retry would mean "never retry").</summary>
public sealed record RetryParameters(int Attempts, string Backoff, int InitialSeconds, int CapSeconds, int WindowSeconds)
{
    public static readonly IReadOnlyList<string> Backoffs = ["exp2", "exp3", "exp4", "exp6", "exp8", "linear", "fixed"];

    /// <summary>Longest retry window the platform offers.</summary>
    public const int MaxWindowSeconds = 72 * 3600;

    public static readonly RetryParameters Default = new(14, "exp4", 5, 43_200, MaxWindowSeconds);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IEnumerable<string> Validate()
    {
        if (Attempts is < 2 or > 30) yield return "Attempts must be between 2 and 30.";
        if (!Backoffs.Contains(Backoff)) yield return $"Backoff must be one of: {string.Join(", ", Backoffs)}.";
        if (InitialSeconds is < 1 or > 3600) yield return "The first retry delay must be between 1 second and 1 hour.";
        if (CapSeconds < InitialSeconds || CapSeconds > 86_400) yield return "The longest delay must be at least the first delay and at most 24 hours.";
        if (WindowSeconds > MaxWindowSeconds) yield return "The retry window can be at most 72 hours.";
        if (WindowSeconds < InitialSeconds) yield return "The retry window is shorter than the first retry delay, so nothing would ever be retried.";
    }

    public RetryPolicy ToPolicy()
    {
        var delays = new List<TimeSpan>();
        for (var k = 1; k < Math.Max(2, Attempts); k++)
        {
            double seconds = Backoff switch
            {
                "linear" => (double)InitialSeconds * k,
                "fixed" => InitialSeconds,
                _ => InitialSeconds * Math.Pow(int.Parse(Backoff[3..], CultureInfo.InvariantCulture), k - 1),
            };
            delays.Add(TimeSpan.FromSeconds(Math.Min(seconds, CapSeconds)));
        }
        return new RetryPolicy(delays, TimeSpan.FromSeconds(WindowSeconds), MaxAttempts: Attempts);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static bool TryParse(string? json, out RetryParameters? parameters)
    {
        parameters = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            parameters = JsonSerializer.Deserialize<RetryParameters>(json, Json);
            return parameters is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
