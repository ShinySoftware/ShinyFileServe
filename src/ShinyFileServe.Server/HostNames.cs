using System.Collections.Concurrent;
using System.Net;

namespace ShinyFileServe.Server;

/// <summary>
/// Turns an address in the activity log into the name of the machine behind it, when the network
/// will say.
/// </summary>
/// <remarks>
/// Resolved when the log is read rather than when it is written, for two reasons. A name written
/// onto the entry would be frozen: addresses are handed out by a DHCP lease and the machine holding
/// one in March is often not the machine holding it in June, so a stored name eventually asserts
/// something false in an audit trail, which is worse than asserting nothing. And the write path is
/// every download, upload and refusal the server handles - putting a DNS round trip in front of the
/// log write would put it in front of the transfer. Reading is the rarer path, it is one screen one
/// administrator opens, and a lookup there can be bounded and given up on.
/// </remarks>
public interface IHostNameResolver
{
    /// <summary>
    /// Names for as many of <paramref name="addresses"/> as can be had inside the budget. Addresses
    /// that are unknown, unresolvable or simply did not answer in time are absent from the result:
    /// the caller shows the address alone, which is what it had anyway.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> Resolve(IEnumerable<string> addresses, CancellationToken ct = default);
}

public class HostNameResolver(TimeProvider time) : IHostNameResolver
{
    /// <summary>
    /// How long a name is believed. Long enough that paging through a log does not re-ask about the
    /// same handful of machines, short enough that a lease moving to another machine corrects itself
    /// within an afternoon.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    /// <summary>
    /// A LAN has tens of machines, not thousands, and this cache exists to stop repeat questions
    /// rather than to hold the world. Past the cap the whole thing is dropped: the addresses worth
    /// keeping are re-learned on the next page, and an eviction policy that has to be tuned is more
    /// machinery than a name lookup is worth.
    /// </summary>
    public const int Capacity = 512;

    /// <summary>
    /// The most addresses one page-load will actually ask the network about. Everything already
    /// cached is free; this caps only the new questions, so a log full of distinct addresses degrades
    /// to showing addresses rather than to a slow response.
    /// </summary>
    public const int MaxLookupsPerRead = 12;

    /// <summary>
    /// The whole budget for a page-load's lookups, run in parallel. A response that waits on DNS is
    /// a response that hangs when DNS is the thing that is broken, which on a private network it
    /// frequently is.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

    readonly ConcurrentDictionary<string, Entry> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A null <see cref="Name"/> is a remembered failure - the point of caching it is to
    /// not ask again for every row of every page.</summary>
    readonly record struct Entry(string? Name, DateTimeOffset ExpiresUtc);

    public async Task<IReadOnlyDictionary<string, string>> Resolve(IEnumerable<string> addresses, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ask = new List<IPAddress>();

        foreach (var address in addresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (String.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address, out var ip))
                continue;

            // A machine does not have a name for itself worth printing next to "this machine", and
            // the lookup is the one most likely to sit there timing out.
            if (IPAddress.IsLoopback(ip))
                continue;

            if (this.cache.TryGetValue(address, out var hit) && hit.ExpiresUtc > now)
            {
                if (hit.Name is { Length: > 0 } cached)
                    found[address] = cached;

                continue;
            }

            if (ask.Count < MaxLookupsPerRead)
                ask.Add(ip);
        }

        if (ask.Count == 0)
            return found;

        if (this.cache.Count >= Capacity)
            this.cache.Clear();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        var lookups = ask.Select(ip => this.Lookup(ip, now, budget.Token)).ToArray();

        // Raced against the clock rather than awaited, because a resolver that has stopped answering
        // does not always honour a token. Whatever has landed by then is used; the stragglers still
        // finish and still write to the cache, so the next page-load has them for nothing.
        await Task.WhenAny(Task.WhenAll(lookups), Task.Delay(Budget, ct));

        for (var i = 0; i < ask.Count; i++)
        {
            if (lookups[i] is { IsCompletedSuccessfully: true, Result: { Length: > 0 } name })
                found[ask[i].ToString()] = name;
        }

        return found;
    }

    async Task<string?> Lookup(IPAddress ip, DateTimeOffset now, CancellationToken ct)
    {
        string? name = null;
        try
        {
            // Handed the address as text on purpose: given a literal, Dns reverses it, which is the
            // question being asked. The IPAddress overload takes no cancellation token.
            var entry = await Dns.GetHostEntryAsync(ip.ToString(), ct);

            // The reverse record is often the address written back at us, which is not news.
            if (!String.IsNullOrWhiteSpace(entry.HostName) && entry.HostName != ip.ToString())
                name = entry.HostName;
        }
        catch (Exception)
        {
            // No record, no resolver, or the budget ran out - all of which mean the same thing to
            // the screen, and none of which are worth failing the log over.
        }

        // Remembered either way. A network with no reverse zone answers nothing for every address
        // on it, and re-asking per page would be the slow path this method exists to avoid.
        this.cache[ip.ToString()] = new Entry(name, now + Lifetime);
        return name;
    }
}
