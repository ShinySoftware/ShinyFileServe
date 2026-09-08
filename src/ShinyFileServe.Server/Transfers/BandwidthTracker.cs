using Microsoft.Extensions.Logging;
using ShinyFileServe.Core.Services;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// Every byte the listener moves, in both directions: a rate for the screen and a running tally for
/// the ledger.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ITransferMonitor"/>, which is about files. A file server
/// also serves its own application, a page of JSON per click and a thumbnail per row, and an
/// operator comparing this against what their router says would find a number that never matched.
/// This counts what crossed the socket, whatever it was for.
/// </remarks>
public interface IBandwidthTracker
{
    /// <summary>
    /// Records one finished request. Called from a request thread, once, so it does the least it
    /// can: two interlocked adds and one dictionary entry.
    /// </summary>
    void Add(string userId, string username, string displayName, long bytesIn, long bytesOut);

    /// <summary>The rate now, the totals since start, and the last minute of samples.</summary>
    BandwidthNow Now();

    /// <summary>Writes whatever has not reached the ledger yet. Called on shutdown, and by the timer.</summary>
    Task Flush(CancellationToken ct = default);
}

public class BandwidthTracker : IBandwidthTracker, IDisposable
{
    /// <summary>One sample a second, and a minute of them - the width of the line on the live screen.</summary>
    public const int HistoryLength = 60;

    static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often counts reach the database. Long enough that a busy second is one write rather than
    /// hundreds, short enough that a report asked for straight after a transfer is not visibly wrong.
    /// </summary>
    static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    readonly Dictionary<string, Pending> pending = [];
    readonly Queue<BandwidthSample> history = new(HistoryLength);
    readonly Lock gate = new();

    readonly IBandwidthMeter meter;
    readonly TimeProvider time;
    readonly ILogger<BandwidthTracker> logger;
    readonly ITimer sampler;
    readonly ITimer flusher;

    long bytesIn;
    long bytesOut;

    long sampledIn;
    long sampledOut;
    long sampledAtTicks;

    double rateIn;
    double rateOut;

    public BandwidthTracker(IBandwidthMeter meter, TimeProvider time, ILogger<BandwidthTracker> logger)
    {
        this.meter = meter;
        this.time = time;
        this.logger = logger;

        this.SinceUtc = time.GetUtcNow();
        this.sampledAtTicks = this.SinceUtc.UtcTicks;

        // Bytes arrive on request threads; a rate is a function of the clock, so it is taken on one.
        // Without this a quiet minute would keep showing the last busy second's number.
        this.sampler = time.CreateTimer(_ => this.Sample(), null, SampleInterval, SampleInterval);
        this.flusher = time.CreateTimer(_ => this.FlushQuietly(), null, FlushInterval, FlushInterval);
    }

    public DateTimeOffset SinceUtc { get; }

    public void Add(string userId, string username, string displayName, long bytesIn, long bytesOut)
    {
        if (bytesIn <= 0 && bytesOut <= 0)
            return;

        Interlocked.Add(ref this.bytesIn, bytesIn);
        Interlocked.Add(ref this.bytesOut, bytesOut);

        lock (this.gate)
        {
            if (this.pending.TryGetValue(userId, out var entry))
            {
                this.pending[userId] = entry with
                {
                    BytesIn = entry.BytesIn + bytesIn,
                    BytesOut = entry.BytesOut + bytesOut
                };
            }
            else
            {
                this.pending[userId] = new Pending(userId, username, displayName, bytesIn, bytesOut);
            }
        }
    }

    public BandwidthNow Now()
    {
        lock (this.gate)
        {
            return new BandwidthNow(
                this.rateIn,
                this.rateOut,
                Interlocked.Read(ref this.bytesIn),
                Interlocked.Read(ref this.bytesOut),
                this.SinceUtc,
                this.history.ToList()
            );
        }
    }

    public async Task Flush(CancellationToken ct = default)
    {
        List<Pending> batch;
        lock (this.gate)
        {
            if (this.pending.Count == 0)
                return;

            batch = this.pending.Values.ToList();
            this.pending.Clear();
        }

        var at = this.time.GetUtcNow();
        var deltas = batch
            .Select(x => new BandwidthDelta(x.UserId, x.Username, x.DisplayName, x.BytesIn, x.BytesOut, at))
            .ToList();

        try
        {
            await this.meter.Record(deltas, ct);
        }
        catch (Exception ex)
        {
            // Put them back rather than lose them: the next flush carries both, and a database that
            // was busy for a moment does not cost an hour of somebody's traffic.
            lock (this.gate)
            {
                foreach (var item in batch)
                {
                    this.pending[item.UserId] = this.pending.TryGetValue(item.UserId, out var current)
                        ? current with
                        {
                            BytesIn = current.BytesIn + item.BytesIn,
                            BytesOut = current.BytesOut + item.BytesOut
                        }
                        : item;
                }
            }

            this.logger.LogWarning(ex, "Could not write bandwidth counts; they will go out with the next flush");
        }
    }

    /// <summary>
    /// The rate over the interval that just passed, measured from the totals rather than accumulated
    /// per request - so a request that took a minute contributes to the seconds it was actually
    /// moving in rather than all at once when it ends.
    /// </summary>
    void Sample()
    {
        var now = this.time.GetUtcNow();
        var totalIn = Interlocked.Read(ref this.bytesIn);
        var totalOut = Interlocked.Read(ref this.bytesOut);

        lock (this.gate)
        {
            var elapsed = (now.UtcTicks - this.sampledAtTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed <= 0)
                return;

            this.rateIn = Math.Max(0, totalIn - this.sampledIn) / elapsed;
            this.rateOut = Math.Max(0, totalOut - this.sampledOut) / elapsed;

            this.sampledIn = totalIn;
            this.sampledOut = totalOut;
            this.sampledAtTicks = now.UtcTicks;

            this.history.Enqueue(new BandwidthSample(now, this.rateIn, this.rateOut));
            while (this.history.Count > HistoryLength)
                this.history.Dequeue();
        }
    }

    void FlushQuietly() => _ = this.Flush();

    public void Dispose()
    {
        this.sampler.Dispose();
        this.flusher.Dispose();

        // Best effort: an app being closed still moved the bytes it moved.
        try
        {
            this.Flush().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Bandwidth counts were lost on shutdown");
        }

        GC.SuppressFinalize(this);
    }

    record Pending(string UserId, string Username, string DisplayName, long BytesIn, long BytesOut);
}
