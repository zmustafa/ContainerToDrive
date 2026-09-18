using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

public enum EndpointKind { Unknown, Private, Public, Mixed }

public sealed record EndpointResolution(string Host, IReadOnlyList<string> Addresses, EndpointKind Kind, DateTimeOffset? ResolvedAt);

public sealed class EndpointResolver
{
    private static readonly IPNetwork[] PrivateNetworks = [IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("fc00::/7")];
    private static readonly IPNetwork[] ReservedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/4"), IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20")
    ];
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _slots = new(4, 4);
    private int _generation;
    internal int RefreshSeconds { get; set; } = 60;
    private readonly ConcurrentDictionary<string, (EndpointResolution Result, DateTimeOffset Expires)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public EndpointResolver() : this(Dns.GetHostAddressesAsync, () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(4)) { }

    internal EndpointResolver(Func<string, CancellationToken, Task<IPAddress[]>> lookup, Func<DateTimeOffset> now, TimeSpan timeout)
    { _lookup = lookup; _now = now; _timeout = timeout; }

    public async Task<EndpointResolution> ResolveAsync(Profile profile, CancellationToken token)
    {
        Validation.ValidateProfile(profile);
        token.ThrowIfCancellationRequested();
        var host = new Uri(profile.Endpoint).DnsSafeHost;
        if (_cache.TryGetValue(host, out var cached) && cached.Expires > _now()) return cached.Result;
        await _slots.WaitAsync(token);
        var generation = Volatile.Read(ref _generation);
        try
        {
            if (_cache.TryGetValue(host, out cached) && cached.Expires > _now()) return cached.Result;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_timeout);
            var addresses = (await _lookup(host, timeout.Token).WaitAsync(timeout.Token))
                .Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Distinct().ToArray();
            token.ThrowIfCancellationRequested();
            var result = new EndpointResolution(host, addresses.Select(address => address.ToString()).Order(StringComparer.Ordinal).ToArray(),
                Classify(addresses), addresses.Length == 0 ? null : _now());
            if (generation == Volatile.Read(ref _generation))
                _cache[host] = (result, _now().AddSeconds(addresses.Length == 0 ? 15 : RefreshSeconds));
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            var result = new EndpointResolution(host, [], EndpointKind.Unknown, null);
            if (generation == Volatile.Read(ref _generation))
                _cache[host] = (result, _now().AddSeconds(15));
            return result;
        }
        finally { _slots.Release(); }
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    public static EndpointKind Classify(IEnumerable<IPAddress> addresses)
    {
        var kinds = addresses.Select(ClassifyAddress).Distinct().ToArray();
        if (kinds.Length == 0 || kinds.Contains(EndpointKind.Unknown)) return EndpointKind.Unknown;
        return kinds.Length == 1 ? kinds[0] : EndpointKind.Mixed;
    }

    private static EndpointKind ClassifyAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (PrivateNetworks.Any(network => network.Contains(address))) return EndpointKind.Private;
        if (ReservedNetworks.Any(network => network.Contains(address))) return EndpointKind.Unknown;
        return address.AddressFamily == AddressFamily.InterNetwork || address.AddressFamily == AddressFamily.InterNetworkV6 && GlobalV6.Contains(address)
            ? EndpointKind.Public : EndpointKind.Unknown;
    }
}