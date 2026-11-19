using System.Net;
using System.Net.Sockets;

namespace InvoiceHub.Webhooks.Core;

public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>A host:port the demo explicitly lets through (local receivers). Never configured outside Development.</summary>
public sealed record EgressAllowance(string Host, int Port);

public sealed record SsrfPolicy(IReadOnlyCollection<EgressAllowance> DevAllowed)
{
    public static readonly SsrfPolicy Strict = new([]);

    public bool IsDevAllowed(string host, int port) =>
        DevAllowed.Any(a => string.Equals(a.Host, host, StringComparison.OrdinalIgnoreCase) && a.Port == port);
}

public sealed record SsrfDecision(bool Allowed, string Code, string Message, IPAddress? Address = null, bool DevException = false)
{
    public static SsrfDecision Allow(IPAddress address, bool devException = false) => new(true, "ok", "Allowed", address, devException);

    public static SsrfDecision Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// Server-side request forgery guard (ASR-10). A webhook URL is tenant-controlled input that our servers will call, so:
/// HTTPS only, no credentials in the URL, no infrastructure ports, and the host must resolve ONLY to public addresses.
/// The check is "resolve, then check, then connect to the address we checked" (the caller pins the returned IP), so a DNS answer
/// that changes between the check and the connection (rebinding) cannot sneak in. Redirects are never followed.
/// The only exception is the demo's explicit host:port allow-list, which exists so the local receivers can be reached in Development.
/// </summary>
public sealed class SsrfGuard(IDnsResolver resolver, SsrfPolicy policy)
{
    public const int MaxUrlLength = 2000;

    // ports of databases, caches, admin and orchestration APIs that no webhook receiver has any business using
    private static readonly HashSet<int> BlockedPorts =
        [21, 22, 23, 25, 110, 135, 139, 143, 445, 1433, 1521, 2375, 2376, 3306, 3389, 5432, 5984, 6379, 9200, 9300, 11211, 27017];

    public async Task<SsrfDecision> EvaluateAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength) return SsrfDecision.Deny("url_invalid", "The URL is empty or too long.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return SsrfDecision.Deny("url_invalid", "The URL is not a valid absolute URL.");
        return await EvaluateAsync(uri, ct);
    }

    public async Task<SsrfDecision> EvaluateAsync(Uri uri, CancellationToken ct) =>
        CheckShape(uri) ?? await ResolveAndCheckAsync(uri.IdnHost, policy.IsDevAllowed(uri.IdnHost, uri.Port), ct);

    /// <summary>The checks that need no DNS: scheme, credentials, port. Null means the URL's shape is acceptable.</summary>
    public SsrfDecision? CheckShape(Uri uri)
    {
        var host = uri.IdnHost;
        var devAllowed = policy.IsDevAllowed(host, uri.Port);

        if (!string.IsNullOrEmpty(uri.UserInfo)) return SsrfDecision.Deny("url_credentials", "Credentials in the URL are not allowed. Use a header instead.");
        if (string.IsNullOrEmpty(host)) return SsrfDecision.Deny("url_invalid", "The URL has no host.");
        if (uri.Scheme != Uri.UriSchemeHttps && !(devAllowed && uri.Scheme == Uri.UriSchemeHttp))
            return SsrfDecision.Deny("https_required", "Only https:// endpoints are accepted.");
        if (!devAllowed && BlockedPorts.Contains(uri.Port)) return SsrfDecision.Deny("port_blocked", $"Port {uri.Port} is not allowed.");
        return null;
    }

    /// <summary>
    /// Called by the HTTP stack at CONNECT time: resolve the name, check every answer, return the one address to connect to.
    /// Doing the check here (and connecting to exactly that address) is what closes the DNS-rebinding window.
    /// </summary>
    public Task<SsrfDecision> ResolveForConnectAsync(string host, int port, CancellationToken ct) =>
        ResolveAndCheckAsync(host, policy.IsDevAllowed(host, port), ct);

    private async Task<SsrfDecision> ResolveAndCheckAsync(string host, bool devAllowed, CancellationToken ct)
    {
        // IP literal: check it directly, no DNS involved
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return devAllowed || !IpRanges.IsBlocked(literal)
                ? SsrfDecision.Allow(literal, devAllowed)
                : SsrfDecision.Deny("address_blocked", "That address is private, loopback, link-local or reserved.");

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await resolver.ResolveAsync(host, ct);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return SsrfDecision.Deny("dns_failed", "The host name does not resolve.");
        }
        if (addresses.Count == 0) return SsrfDecision.Deny("dns_failed", "The host name does not resolve.");

        // IPv4 first: a local receiver is typically bound to 127.0.0.1 only
        if (devAllowed) return SsrfDecision.Allow(addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0], devException: true);

        // every answer must be public: one private address among public ones is the classic rebinding trick
        if (addresses.Any(IpRanges.IsBlocked)) return SsrfDecision.Deny("address_blocked", "The host resolves to a private, loopback, link-local or reserved address.");

        var chosen = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        return SsrfDecision.Allow(chosen);
    }
}

public static class IpRanges
{
    private static readonly (IPAddress Network, int Prefix)[] V4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8),        // "this" network
        (IPAddress.Parse("10.0.0.0"), 8),       // private
        (IPAddress.Parse("100.64.0.0"), 10),    // carrier-grade NAT
        (IPAddress.Parse("127.0.0.0"), 8),      // loopback
        (IPAddress.Parse("169.254.0.0"), 16),   // link-local, including the cloud metadata address 169.254.169.254
        (IPAddress.Parse("172.16.0.0"), 12),    // private
        (IPAddress.Parse("192.0.0.0"), 24),     // IETF protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24),     // documentation
        (IPAddress.Parse("192.88.99.0"), 24),   // 6to4 relay
        (IPAddress.Parse("192.168.0.0"), 16),   // private
        (IPAddress.Parse("198.18.0.0"), 15),    // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24),  // documentation
        (IPAddress.Parse("203.0.113.0"), 24),   // documentation
        (IPAddress.Parse("224.0.0.0"), 4),      // multicast
        (IPAddress.Parse("240.0.0.0"), 4),      // reserved, includes broadcast
    ];

    private static readonly (IPAddress Network, int Prefix)[] V6 =
    [
        (IPAddress.Parse("::"), 128),           // unspecified
        (IPAddress.Parse("::1"), 128),          // loopback
        (IPAddress.Parse("100::"), 64),         // discard-only
        (IPAddress.Parse("2001::"), 32),        // Teredo
        (IPAddress.Parse("2001:db8::"), 32),    // documentation
        (IPAddress.Parse("fc00::"), 7),         // unique local
        (IPAddress.Parse("fe80::"), 10),        // link-local
        (IPAddress.Parse("fec0::"), 10),        // site-local (deprecated)
        (IPAddress.Parse("ff00::"), 8),         // multicast
    ];

    public static bool IsBlocked(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv4 hidden inside an IPv6 address must be judged as that IPv4 address
            if (address.IsIPv4MappedToIPv6) return IsBlocked(address.MapToIPv4());
            if (TryEmbeddedV4(address, out var embedded)) return IsBlocked(embedded);
            return V6.Any(r => InRange(address, r.Network, r.Prefix));
        }
        return V4.Any(r => InRange(address, r.Network, r.Prefix));
    }

    /// <summary>NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address; both can be used to reach internal IPv4 hosts.</summary>
    private static bool TryEmbeddedV4(IPAddress address, out IPAddress v4)
    {
        var b = address.GetAddressBytes();
        if (InRange(address, IPAddress.Parse("64:ff9b::"), 96)) { v4 = new IPAddress(b[12..16]); return true; }
        if (b[0] == 0x20 && b[1] == 0x02) { v4 = new IPAddress(b[2..6]); return true; }
        v4 = IPAddress.None;
        return false;
    }

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        if (a.Length != n.Length) return false;
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++) if (a[i] != n[i]) return false;
        var rest = prefix % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}
