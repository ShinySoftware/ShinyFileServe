namespace ShinyFileServe.Server.Transfers;

/// <summary>Thrown when an upload passes the most it may be.</summary>
public sealed class UploadLimitExceededException(long limit)
    : Exception($"The upload is larger than the {limit} bytes it may be.")
{
    public long Limit { get; } = limit;
}

/// <summary>
/// Reads through to <paramref name="inner"/> and throws <see cref="UploadLimitExceededException"/>
/// as soon as more than <paramref name="limit"/> bytes have come out of it. An upload's size is
/// usually unknown until its last byte, and this stops one at the limit instead of writing the whole
/// file to disk only to delete it. Never disposes the inner stream: a multipart part's body belongs
/// to the reader that made it.
/// </summary>
sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    long read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => this.read;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
        => this.Count(inner.Read(buffer));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => this.Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    int Count(int bytes)
    {
        this.read += bytes;
        if (this.read > limit)
            throw new UploadLimitExceededException(limit);

        return bytes;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
