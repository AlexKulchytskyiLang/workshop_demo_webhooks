namespace InvoiceHub.Webhooks.Core;

public enum AttemptOutcome
{
    Success,

    /// <summary>Failure the receiver can recover from (5xx, timeout, network error, 429, 3xx, other 4xx): retry. Counts for the breaker only when it points at a sick receiver.</summary>
    Retry,

    /// <summary>410 Gone: the receiver says the endpoint no longer exists. Hold the delivery and disable the endpoint.</summary>
    Gone,
}

public sealed record Classification(AttemptOutcome Outcome, bool CountsTowardBreaker, bool CountsTowardAutoDisable, string Reason, TimeSpan? RetryAfter = null);

public static class ResponseClassifier
{
    /// <summary>
    /// 2xx = success. 3xx = failure (redirects are never followed: they are an SSRF vector). 410 = gone.
    /// 429 and 503 honour Retry-After. 5xx, timeouts and network errors are "the receiver is sick": they feed the circuit breaker and
    /// auto-disable. Other 4xx are retried (the receiver may fix its config) but do NOT feed auto-disable: a 401 is a misconfiguration, not a dead server.
    /// </summary>
    public static Classification Classify(int? statusCode, bool timedOut, bool networkError, string? retryAfterHeader, DateTimeOffset now)
    {
        if (timedOut) return new(AttemptOutcome.Retry, true, true, "timeout");
        if (networkError || statusCode is null) return new(AttemptOutcome.Retry, true, true, "network_error");

        var code = statusCode.Value;
        var retryAfter = ParseRetryAfter(retryAfterHeader, now);
        return code switch
        {
            >= 200 and < 300 => new(AttemptOutcome.Success, false, false, "ok"),
            410 => new(AttemptOutcome.Gone, false, false, "gone"),
            429 => new(AttemptOutcome.Retry, false, false, "rate_limited", retryAfter),
            503 => new(AttemptOutcome.Retry, true, true, "service_unavailable", retryAfter),
            >= 500 => new(AttemptOutcome.Retry, true, true, "server_error", retryAfter),
            >= 300 and < 400 => new(AttemptOutcome.Retry, false, false, "redirect_not_followed"),
            408 => new(AttemptOutcome.Retry, true, true, "request_timeout"),
            401 or 403 => new(AttemptOutcome.Retry, false, false, "unauthorized_by_receiver"),
            _ => new(AttemptOutcome.Retry, false, false, "client_error"),
        };
    }

    /// <summary>Retry-After is either delta-seconds or an HTTP date. A hostile receiver must not park us for a year: capped at 12 h.</summary>
    public static TimeSpan? ParseRetryAfter(string? header, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        TimeSpan? value = null;
        if (int.TryParse(header, out var seconds)) value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        else if (DateTimeOffset.TryParse(header, out var date)) value = date > now ? date - now : TimeSpan.Zero;
        return value is { } v ? (v > TimeSpan.FromHours(12) ? TimeSpan.FromHours(12) : v) : null;
    }
}
