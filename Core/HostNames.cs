using System.Collections.Concurrent;
using System.Net;

namespace AVCoders.Core;

public static class HostNames
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<IPAddress, Lookup> Lookups = new();
    private static readonly ConcurrentDictionary<IPAddress, bool> LookupsInFlight = new();
    private static readonly SemaphoreSlim ResolverLock = new(1, 1);

    private record Lookup(string? HostName, DateTimeOffset CompletedAt);

    public static string? Get(IPAddress address)
    {
        Lookups.TryGetValue(address, out var lookup);
        if (lookup != null && DateTimeOffset.UtcNow - lookup.CompletedAt < CacheLifetime)
            return lookup.HostName;

        if (LookupsInFlight.TryAdd(address, true))
            _ = Task.Run(() => Resolve(address));
        return lookup?.HostName;
    }

    private static async Task Resolve(IPAddress address)
    {
        string? hostName;
        await ResolverLock.WaitAsync();
        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            hostName = entry.HostName;
        }
        catch (Exception)
        {
            hostName = null;
        }
        finally
        {
            ResolverLock.Release();
        }

        Lookups[address] = new Lookup(hostName, DateTimeOffset.UtcNow);
        LookupsInFlight.TryRemove(address, out _);
    }
}
