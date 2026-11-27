using System.Net;
using System.Net.Sockets;

namespace RelayStack.Platform.Security;

public enum AddressClass
{
    Public,

    /// <summary>Loopback, RFC 1918, CGNAT, ULA and similar. Blocked unless an explicit development exception applies.</summary>
    Private,

    /// <summary>Never reachable, not even for the development receiver: link-local (incl. cloud metadata), unspecified, multicast, reserved.</summary>
    AlwaysBlocked,
}

/// <summary>Classifies IP addresses for SSRF protection. Pure function; exhaustively unit tested.</summary>
public static class AddressClassifier
{
    private static readonly (IPAddress Network, int Prefix, AddressClass Class)[] V4Ranges =
    [
        (IPAddress.Parse("0.0.0.0"), 8, AddressClass.AlwaysBlocked),        // "this network"
        (IPAddress.Parse("169.254.0.0"), 16, AddressClass.AlwaysBlocked),   // link-local, incl. 169.254.169.254 metadata
        (IPAddress.Parse("224.0.0.0"), 4, AddressClass.AlwaysBlocked),      // multicast
        (IPAddress.Parse("240.0.0.0"), 4, AddressClass.AlwaysBlocked),      // reserved + 255.255.255.255 broadcast
        (IPAddress.Parse("127.0.0.0"), 8, AddressClass.Private),            // loopback
        (IPAddress.Parse("10.0.0.0"), 8, AddressClass.Private),
        (IPAddress.Parse("172.16.0.0"), 12, AddressClass.Private),
        (IPAddress.Parse("192.168.0.0"), 16, AddressClass.Private),
        (IPAddress.Parse("100.64.0.0"), 10, AddressClass.Private),          // CGNAT (also Alibaba metadata 100.100.100.200)
        (IPAddress.Parse("192.0.0.0"), 24, AddressClass.Private),           // IETF protocol assignments
        (IPAddress.Parse("192.0.2.0"), 24, AddressClass.Private),           // documentation
        (IPAddress.Parse("198.18.0.0"), 15, AddressClass.Private),          // benchmarking
        (IPAddress.Parse("198.51.100.0"), 24, AddressClass.Private),        // documentation
        (IPAddress.Parse("203.0.113.0"), 24, AddressClass.Private),         // documentation
    ];

    private static readonly (IPAddress Network, int Prefix, AddressClass Class)[] V6Ranges =
    [
        (IPAddress.Parse("fd00:ec2::254"), 128, AddressClass.AlwaysBlocked), // AWS IMDS over IPv6
        (IPAddress.Parse("fe80::"), 10, AddressClass.AlwaysBlocked),         // link-local
        (IPAddress.Parse("ff00::"), 8, AddressClass.AlwaysBlocked),          // multicast
        (IPAddress.Parse("::1"), 128, AddressClass.Private),                 // loopback
        (IPAddress.Parse("fc00::"), 7, AddressClass.Private),                // unique local
        (IPAddress.Parse("fec0::"), 10, AddressClass.Private),               // deprecated site-local
        (IPAddress.Parse("2001:db8::"), 32, AddressClass.Private),           // documentation
        (IPAddress.Parse("100::"), 64, AddressClass.Private),                // discard-only
        (IPAddress.Parse("64:ff9b:1::"), 48, AddressClass.Private),          // local-use NAT64
        (IPAddress.Parse("2001::"), 23, AddressClass.Private),               // IETF protocol assignments (incl. Teredo 2001::/32)
    ];

    public static AddressClass Classify(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return Classify(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            foreach (var (net, prefix, cls) in V4Ranges)
            {
                if (InRange(address, net, prefix))
                {
                    return cls;
                }
            }

            return AddressClass.Public;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return AddressClass.AlwaysBlocked;
        }

        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
        {
            return AddressClass.AlwaysBlocked;
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return AddressClass.Private;
        }

        var bytes = address.GetAddressBytes();

        // Addresses that embed an IPv4 address are judged by the embedded address (and never better than it).
        if (InRange(address, IPAddress.Parse("64:ff9b::"), 96))
        {
            return Classify(new IPAddress(bytes.AsSpan(12, 4)));
        }

        if (InRange(address, IPAddress.Parse("2002::"), 16))
        {
            var embedded = Classify(new IPAddress(bytes.AsSpan(2, 4)));
            return embedded == AddressClass.Public ? AddressClass.Public : embedded;
        }

        if (InRange(address, IPAddress.Parse("::"), 96))
        {
            // Deprecated IPv4-compatible addresses (::a.b.c.d). Not routable on the public internet.
            return AddressClass.AlwaysBlocked;
        }

        foreach (var (net, prefix, cls) in V6Ranges)
        {
            if (InRange(address, net, prefix))
            {
                return cls;
            }
        }

        return AddressClass.Public;
    }

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i])
            {
                return false;
            }
        }

        var rem = prefix % 8;
        if (rem == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rem));
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}

public sealed class OutboundRequestBlockedException(string message) : Exception(message);

/// <summary>Decides which outbound connections are allowed. Production behaviour has no exceptions.</summary>
public sealed class OutboundPolicy
{
    /// <summary>Request option that marks a request as eligible for the development receiver exception.</summary>
    public static readonly HttpRequestOptionsKey<bool> AllowDevelopmentReceiver = new("relaystack.allow-dev-receiver");

    private readonly HashSet<string> _devAllowlist;

    public OutboundPolicy(IEnumerable<string> developmentAllowlist, bool isDevelopmentHost)
    {
        _devAllowlist = new HashSet<string>(developmentAllowlist.Select(e => e.Trim().ToLowerInvariant()), StringComparer.Ordinal);
        if (_devAllowlist.Count > 0 && !isDevelopmentHost)
        {
            throw new InvalidOperationException(
                "RelayStack:Delivery:DevelopmentReceiverAllowlist must be empty outside the Development environment.");
        }

        foreach (var entry in _devAllowlist)
        {
            var parts = entry.Split(':');
            if (parts.Length != 2 || parts[0].Length == 0 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535)
            {
                throw new InvalidOperationException($"Invalid development allowlist entry '{entry}'. Expected exact 'host:port'.");
            }
        }
    }

    public static OutboundPolicy Production { get; } = new([], isDevelopmentHost: false);

    public bool IsDevelopmentReceiver(string host, int port) => _devAllowlist.Contains($"{host.ToLowerInvariant()}:{port}");

    /// <summary>Throws if the resolved address may not be contacted for this host/port.</summary>
    public void EnsureAllowed(IPAddress address, string host, int port, bool requestAllowsDevReceiver)
    {
        var cls = AddressClassifier.Classify(address);
        if (cls == AddressClass.Public)
        {
            return;
        }

        if (cls == AddressClass.Private && requestAllowsDevReceiver && IsDevelopmentReceiver(host, port))
        {
            return;
        }

        throw new OutboundRequestBlockedException($"Destination address for '{host}' is not publicly routable ({cls}).");
    }

    /// <summary>Validation when a destination URL is created or updated. The connect-time check is still authoritative.</summary>
    public string? ValidateDestinationUrl(string? url, bool environmentIsDevelopment)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return "url must be an absolute URL of at most 2048 characters.";
        }

        if (uri.UserInfo.Length > 0)
        {
            return "url must not contain credentials.";
        }

        if (uri.Fragment.Length > 0)
        {
            return "url must not contain a fragment.";
        }

        var devReceiver = environmentIsDevelopment && IsDevelopmentReceiver(uri.Host, uri.Port);
        if (uri.Scheme != Uri.UriSchemeHttps && !(devReceiver && uri.Scheme == Uri.UriSchemeHttp))
        {
            return "url must use https.";
        }

        if (!devReceiver && uri.Port is not (443 or 8443))
        {
            return "url port must be 443 or 8443.";
        }

        if (!devReceiver && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return "url must not point at a loopback host.";
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && AddressClassifier.Classify(literal) != AddressClass.Public && !devReceiver)
        {
            return "url must not point at a private, loopback, link-local or metadata address.";
        }

        return null;
    }
}

/// <summary>
/// Builds the only HttpClient used for customer-controlled URLs.
/// DNS is resolved inside the connect callback and the socket connects to the exact address that was
/// validated, which closes the DNS-rebinding window between "check" and "connect". Proxies and redirects
/// are disabled so neither can be used to reach an internal address.
/// </summary>
public static class SafeHttpClientFactory
{
    public static HttpClient Create(OutboundPolicy policy, TimeSpan connectTimeout, Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        resolver ??= (host, ct) => Dns.GetHostAddressesAsync(host, ct);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = connectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxResponseHeadersLength = 16, // KiB
            MaxConnectionsPerServer = 16,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;
                var allowDev = context.InitialRequestMessage.Options.TryGetValue(OutboundPolicy.AllowDevelopmentReceiver, out var a) && a;

                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await resolver(host, ct);
                if (addresses.Length == 0)
                {
                    throw new OutboundRequestBlockedException($"Host '{host}' did not resolve.");
                }

                // Every resolved address must be allowed; a mixed public/private answer is treated as hostile.
                foreach (var address in addresses)
                {
                    policy.EnsureAllowed(address, host, port, allowDev);
                }

                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
