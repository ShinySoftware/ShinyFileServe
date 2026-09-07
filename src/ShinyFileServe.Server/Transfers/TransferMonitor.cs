using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// What is moving through the server right now, and what has just finished.
/// </summary>
/// <remarks>
/// A transfer is registered once and thereafter only counted. The per-chunk path is one interlocked
/// add and nothing else - it runs once per buffer on every concurrent transfer, and a lock there
/// would make the monitor the slowest part of a file server. Everything that costs something -
/// sampling for speed, ageing the history, building a snapshot - happens on the sweep or when an
/// administrator asks, under a single lock.
/// </remarks>
public interface ITransferMonitor
{
    /// <summary>
    /// Registers a transfer that is starting. Dispose the handle when the request is over: that is
    /// the one moment which happens whether it finished, failed, or the client walked away.
    /// </summary>
    ITransferHandle Begin(
        TransferDirection direction,
        string userId,
        string username,
        string displayName,
        string folderId,
        string folderName,
        string path,
        long? totalBytes
    );

    TransferSnapshot Snapshot();
}

/// <summary>
/// One transfer's progress. Disposing closes it off; a handle that was never completed is recorded
/// as abandoned, because a stream that stops being read is exactly what a departed client looks like.
/// </summary>
public interface ITransferHandle : IDisposable
{
    void Advance(long bytes);

    /// <summary>The total, once it becomes knowable. An upload rarely declares one up front.</summary>
    void SetTotal(long totalBytes);

    void Complete();

    void Fail(string problem);
}

/// <summary>Both halves of the picture, taken at the same instant so they cannot disagree.</summary>
public record TransferSnapshot(IReadOnlyList<LiveTransferInfo> Live, IReadOnlyList<FinishedTransferInfo> Finished);

public class TransferMonitor : ITransferMonitor, IDisposable
{
    /// <summary>
    /// How many finished transfers are kept. A server left running for a month must not accumulate
    /// history, and nobody scrolls past the last screenful of one anyway.
    /// </summary>
    public const int HistoryLimit = 60;

    /// <summary>The other end of the same bargain: old enough is gone even if the list is short.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Speed is the slope across this many samples. Long enough that a single slow buffer does not
    /// show up as a stall, short enough that a genuine stall is visible within a few seconds.
    /// </summary>
    const int Samples = 6;

    static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>A snapshot may sample too, so two samples an instant apart are collapsed into one.</summary>
    static readonly TimeSpan MinimumSampleGap = TimeSpan.FromMilliseconds(250);

    readonly Dictionary<string, Entry> live = [];
    readonly List<FinishedTransferInfo> finished = [];
    readonly Lock gate = new();
    readonly TimeProvider time;
    readonly ITimer sweep;

    long sequence;

    public TransferMonitor(TimeProvider time)
    {
        this.time = time;

        // Bytes arrive on request threads, but the shape of a speed curve is a function of the
        // clock, so the samples that produce it are taken on one.
        this.sweep = time.CreateTimer(_ => this.Sweep(), null, SampleInterval, SampleInterval);
    }

    public ITransferHandle Begin(
        TransferDirection direction,
        string userId,
        string username,
        string displayName,
        string folderId,
        string folderName,
        string path,
        long? totalBytes)
    {
        var now = this.time.GetUtcNow();
        var id = Interlocked.Increment(ref this.sequence).ToString("x");
        var entry = new Entry(this, id, direction, userId, username, displayName, folderId, folderName, path, totalBytes, now);

        lock (this.gate)
        {
            this.live[id] = entry;
            entry.Sample(now);
        }

        return entry;
    }

    public TransferSnapshot Snapshot()
    {
        var now = this.time.GetUtcNow();

        lock (this.gate)
        {
            this.Trim(now);

            var running = new List<LiveTransferInfo>(this.live.Count);
            foreach (var entry in this.live.Values)
            {
                entry.Sample(now);
                running.Add(entry.ToLive());
            }

            running.Sort(static (a, b) => b.StartedUtc.CompareTo(a.StartedUtc));
            return new TransferSnapshot(running, this.finished.ToList());
        }
    }

    void Sweep()
    {
        var now = this.time.GetUtcNow();

        lock (this.gate)
        {
            foreach (var entry in this.live.Values)
                entry.Sample(now);

            this.Trim(now);
        }
    }

    /// <summary>Called under the lock. Bounds the history by age first, then by count.</summary>
    void Trim(DateTimeOffset now)
    {
        var cutoff = now - HistoryWindow;
        this.finished.RemoveAll(x => x.FinishedUtc < cutoff);

        if (this.finished.Count > HistoryLimit)
            this.finished.RemoveRange(HistoryLimit, this.finished.Count - HistoryLimit);
    }

    void Finish(Entry entry)
    {
        var now = this.time.GetUtcNow();

        lock (this.gate)
        {
            this.live.Remove(entry.Id);

            // Newest first, which is the order it is read in and the order the cap trims from.
            this.finished.Insert(0, entry.ToFinished(now));
            this.Trim(now);
        }
    }

    public void Dispose()
    {
        this.sweep?.Dispose();
        GC.SuppressFinalize(this);
    }

    // -----------------------------------------------------------------------

    sealed class Entry(
        TransferMonitor owner,
        string id,
        TransferDirection direction,
        string userId,
        string username,
        string displayName,
        string folderId,
        string folderName,
        string path,
        long? totalBytes,
        DateTimeOffset startedUtc
    ) : ITransferHandle
    {
        // The ring is only ever touched under the owner's lock, so it needs no synchronisation of
        // its own; the counters are read there too but written from request threads.
        readonly long[] sampleBytes = new long[Samples];
        readonly long[] sampleTicks = new long[Samples];
        int sampleCount;
        int sampleNext;

        long bytes;
        long total = totalBytes ?? -1;
        int state;      // 0 running, 1 completed, 2 failed
        int closed;
        string? problem;

        public string Id => id;

        public void Advance(long count)
        {
            if (count > 0)
                Interlocked.Add(ref this.bytes, count);
        }

        public void SetTotal(long totalBytes) => Interlocked.Exchange(ref this.total, totalBytes);

        public void Complete() => Interlocked.CompareExchange(ref this.state, 1, 0);

        public void Fail(string reason)
        {
            this.problem = reason;
            Interlocked.CompareExchange(ref this.state, 2, 0);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.closed, 1) == 0)
                owner.Finish(this);
        }

        /// <summary>Called under the owner's lock.</summary>
        public void Sample(DateTimeOffset now)
        {
            var ticks = now.UtcTicks;
            if (this.sampleCount > 0)
            {
                var last = this.sampleTicks[(this.sampleNext - 1 + Samples) % Samples];
                if (ticks - last < MinimumSampleGap.Ticks)
                    return;
            }

            this.sampleBytes[this.sampleNext] = Interlocked.Read(ref this.bytes);
            this.sampleTicks[this.sampleNext] = ticks;
            this.sampleNext = (this.sampleNext + 1) % Samples;
            if (this.sampleCount < Samples)
                this.sampleCount++;
        }

        /// <summary>Called under the owner's lock.</summary>
        public LiveTransferInfo ToLive()
        {
            var moved = Interlocked.Read(ref this.bytes);
            var size = Interlocked.Read(ref this.total);

            return new LiveTransferInfo(
                id, userId, username, displayName, folderId, folderName, path, direction,
                moved,
                size < 0 ? null : size,
                startedUtc,
                this.Speed()
            );
        }

        /// <summary>Called under the owner's lock.</summary>
        public FinishedTransferInfo ToFinished(DateTimeOffset now)
        {
            var moved = Interlocked.Read(ref this.bytes);
            var size = Interlocked.Read(ref this.total);

            // Never completed and never explicitly failed means the stream simply stopped being
            // read, which is what a client closing its connection looks like from here.
            var outcome = this.state switch
            {
                1 => TransferOutcome.Completed,
                2 => TransferOutcome.Failed,
                _ => TransferOutcome.Abandoned
            };

            return new FinishedTransferInfo(
                id, userId, username, displayName, folderId, folderName, path, direction,
                moved,
                size < 0 ? null : size,
                startedUtc,
                now,
                outcome,
                this.problem
            );
        }

        double Speed()
        {
            if (this.sampleCount < 2)
                return 0;

            var newest = (this.sampleNext - 1 + Samples) % Samples;
            var oldest = (this.sampleNext - this.sampleCount + Samples) % Samples;

            var elapsed = this.sampleTicks[newest] - this.sampleTicks[oldest];
            if (elapsed <= 0)
                return 0;

            var moved = this.sampleBytes[newest] - this.sampleBytes[oldest];
            return moved <= 0 ? 0 : moved / ((double)elapsed / TimeSpan.TicksPerSecond);
        }
    }
}
