using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using InvoiceHub.Webhooks.Core;
using Microsoft.Extensions.Options;

namespace InvoiceHub.Webhooks.Sending;

internal sealed record SendRequest(Uri Url, byte[] Body, IReadOnlyDictionary<string, string> Headers, TimeSpan Timeout);

/// <param name="Blocked">Set when the egress guard refused to connect (private address, bad scheme...). Nothing was sent.</param>
internal sealed record SendResult(
    int? StatusCode, bool TimedOut, bool NetworkError, string? Error, string? ResponseSnippet, string? RetryAfter, TimeSpan Duration, SsrfDecision? Blocked = null);

internal interface IWebhookSender
{
    Task<SendResult> SendAsync(SendRequest request, CancellationToken ct);
}

/// <summary>The egress guard said no at connect time.</summary>
internal sealed class EgressBlockedException(SsrfDecision decision) : Exception(decision.Message)
{
    public SsrfDecision Decision { get; } = decision;
}

/// <summary>
/// Sends one signed request. The safety properties live here (ASR-10, ASR-14): the connection goes ONLY to the address the SSRF guard
/// just validated (resolve, check, connect to that IP: DNS rebinding cannot swap it), redirects are never followed, no proxy, the
/// request has a hard timeout, and only the first few KB of the reply are ever read.
/// </summary>
internal sealed class WebhookSender : IWebhookSender, IDisposable
{
    public static readonly ActivitySource Source = new("InvoiceHub.Webhooks");

    private readonly HttpClient _http;
    private readonly SsrfGuard _guard;
    private readonly int _responseCap;

    public WebhookSender(SsrfGuard guard, IOptions<WebhooksOptions> options)
    {
        _guard = guard;
        _responseCap = options.Value.Delivery.ResponseCapBytes;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1), // a fresh DNS check at least every minute
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxConnectionsPerServer = 20,
            ConnectCallback = ConnectAsync,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeout below
    }

    public async Task<SendResult> SendAsync(SendRequest request, CancellationToken ct)
    {
        // shape first (scheme, credentials, port): cheap, no DNS. The address check happens at connect time, below.
        if (_guard.CheckShape(request.Url) is { } badShape)
            return new SendResult(null, false, false, $"Blocked by egress policy: {badShape.Message}", null, null, TimeSpan.Zero, badShape);

        var started = Stopwatch.GetTimestamp();
        using var activity = Source.StartActivity("webhook.send", ActivityKind.Client);
        activity?.SetTag("http.request.method", "POST");
        activity?.SetTag("server.address", request.Url.Host);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, request.Url) { Content = new ByteArrayContent(request.Body) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            foreach (var (name, value) in request.Headers) message.Headers.TryAddWithoutValidation(name, value);
            // no traceparent by hand: the HTTP client sends the one of its own span, a child of "webhook.send", so the receiver sees ONE header of the same trace

            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var snippet = await ReadSnippetAsync(response, timeout.Token);
            var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            return new SendResult((int)response.StatusCode, false, false, null, snippet, retryAfter, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(null, true, false, $"No answer within {request.Timeout.TotalSeconds:0} s.", null, null, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception e) when (Find<EgressBlockedException>(e) is { } blocked)
        {
            return new SendResult(null, false, false, $"Blocked by egress policy: {blocked.Decision.Message}", null, null, Stopwatch.GetElapsedTime(started), blocked.Decision);
        }
        catch (HttpRequestException e)
        {
            activity?.SetStatus(ActivityStatusCode.Error, e.Message);
            return new SendResult(null, false, true, Truncate(Describe(e), 400), null, null, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[_responseCap];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        if (read == 0) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        // keep what a human can read in the log: drop control characters, cap the length
        var cleaned = new string(text.Select(c => char.IsControl(c) && c is not ('\n' or '\t') ? ' ' : c).ToArray());
        return Truncate(cleaned, 1000);
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var decision = await _guard.ResolveForConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct);
        if (!decision.Allowed) throw new EgressBlockedException(decision);

        var socket = new Socket(decision.Address!.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(decision.Address, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static T? Find<T>(Exception e) where T : Exception
    {
        for (var current = e; current is not null; current = current.InnerException) if (current is T found) return found;
        return null;
    }

    private static string Describe(HttpRequestException e) => e.InnerException is { } inner ? $"{e.Message} ({inner.Message})" : e.Message;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose() => _http.Dispose();
}
