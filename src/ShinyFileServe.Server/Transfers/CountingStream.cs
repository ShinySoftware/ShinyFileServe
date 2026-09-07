namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// A read-through byte counter. Both directions are reads from this end - a download is read off the
/// disk as fast as the client takes it, and an upload is read off the socket - so one wrapper covers
/// both, and what it counts is what actually moved rather than what was supposed to.
/// </summary>
/// <param name="inner">The stream being read.</param>
/// <param name="handle">Where the count goes.</param>
/// <param name="expectedTotal">
/// Evaluated once, just before the first read, for a total that is only knowable by then. A range
/// request is the reason: how much is being sent is decided after the stream is opened.
/// </param>
/// <param name="leaveInnerOpen">
/// True when something else owns the inner stream. A multipart part's body belongs to the reader
/// that produced it and closing it would end the whole upload, not just this file.
/// </param>
/// <param name="ownsHandle">
/// True when disposing this stream is the end of the transfer. False when the caller decides the
/// outcome itself and disposes the handle in its own time.
/// </param>
sealed class CountingStream(
    Stream inner,
    ITransferHandle handle,
    Func<long?>? expectedTotal = null,
    bool leaveInnerOpen = false,
    bool ownsHandle = true
) : Stream
{
    bool started;
    bool closed;
    long expected = -1;
    long counted;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override int Read(byte[] buffer, int offset, int count)
    {
        this.Start();
        var read = inner.Read(buffer, offset, count);
        this.Count(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        this.Start();
        var read = inner.Read(buffer);
        this.Count(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        this.Start();
        var read = await inner.ReadAsync(buffer, ct);
        this.Count(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        this.Start();
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), ct);
        this.Count(read);
        return read;
    }

    void Start()
    {
        if (this.started)
            return;

        this.started = true;
        if (expectedTotal?.Invoke() is { } total)
        {
            this.expected = total;
            handle.SetTotal(total);
        }
    }

    void Count(int read)
    {
        if (read <= 0)
            return;

        this.counted += read;
        handle.Advance(read);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !this.closed)
        {
            this.closed = true;
            this.CloseTransfer();

            if (!leaveInnerOpen)
                inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!this.closed)
        {
            this.closed = true;
            this.CloseTransfer();

            if (!leaveInnerOpen)
                await inner.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Dispose is the only moment guaranteed to happen. A client that walks away mid-stream never
    /// reaches the end of the copy, so a transfer closed off anywhere else would sit in the live
    /// list forever - and the shortfall against the expected total is what says it went wrong.
    /// </summary>
    void CloseTransfer()
    {
        if (!ownsHandle)
            return;

        if (this.expected >= 0 && this.counted >= this.expected)
            handle.Complete();

        // Otherwise it is left alone deliberately: neither completed nor explicitly failed is what
        // the monitor records as abandoned, which is what a dropped connection is.
        handle.Dispose();
    }
}
