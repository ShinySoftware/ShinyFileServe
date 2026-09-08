using System.IO.Pipelines;
using System.Security.Claims;
using Shiny.Net.HttpServer;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// Counts the bodies of every request and every response, and files them under whoever made them.
/// </summary>
/// <remarks>
/// Wrapping the streams rather than reading <c>Content-Length</c>: a declared length is what was
/// promised, and the interesting cases are the ones where the promise was not kept - a download the
/// client walked away from, a chunked response that never declared one at all. What is counted is
/// what the socket carried.
/// <para>
/// It sits first in the pipeline so it sees responses nothing else does - a 401 from authentication,
/// the sign-in page itself - and reads the identity on the way out, by which time authentication has
/// run and said who it was.
/// </para>
/// </remarks>
public class BandwidthMeteringMiddleware(IBandwidthTracker tracker) : IHttpMiddleware
{
    public async ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        var counter = new RequestCounter();

        context.Request.Body = new CountingReadStream(context.Request.Body, counter);
        context.Response.Bind(new CountingBodyControl(context.Response.BodyControl, counter));

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var (id, username, display) = Identify(context);
            tracker.Add(id, username, display, counter.In, counter.Out);
        }
    }

    /// <summary>
    /// Whose traffic this was. Everything before a sign-in - the application itself, the sign-in
    /// page, the 401 that sent them there - is real bandwidth with no account behind it, so it goes
    /// to one well-known row rather than being dropped and making the total disagree with the link.
    /// </summary>
    static (string Id, string Username, string DisplayName) Identify(HttpContext context)
    {
        if (context.User.Identity is { IsAuthenticated: true })
        {
            var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (id is { Length: > 0 })
            {
                var username = context.User.Identity.Name ?? "";
                return (id, username, context.User.FindFirst("display_name")?.Value ?? username);
            }
        }

        return (BandwidthUsers.Anonymous, "anonymous", BandwidthUsers.AnonymousDisplayName);
    }
}

/// <summary>One request's two numbers. Written from whichever thread the body is being moved on.</summary>
sealed class RequestCounter
{
    long inBytes;
    long outBytes;

    public long In => Interlocked.Read(ref this.inBytes);
    public long Out => Interlocked.Read(ref this.outBytes);

    public void AddIn(long count)
    {
        if (count > 0)
            Interlocked.Add(ref this.inBytes, count);
    }

    public void AddOut(long count)
    {
        if (count > 0)
            Interlocked.Add(ref this.outBytes, count);
    }
}

/// <summary>The request body, counted as the handler reads it.</summary>
sealed class CountingReadStream(Stream inner, RequestCounter counter) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        counter.AddIn(read);
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        counter.AddIn(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        counter.AddIn(read);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        counter.AddIn(read);
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    // The connection owns the request body. Disposing it here would close the socket's reader out
    // from under a keep-alive connection that has another request coming.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

/// <summary>
/// The response body, counted as it goes out. Both surfaces are wrapped, because a handler may use
/// either: the helpers on <c>HttpResponse</c> write to the stream, and a static file writes to the
/// pipe. Each byte passes through exactly one of them.
/// </summary>
sealed class CountingBodyControl(IResponseBodyControl inner, RequestCounter counter) : IResponseBodyControl
{
    CountingWriteStream? stream;
    CountingPipeWriter? writer;

    public bool HasStarted => inner.HasStarted;

    public Stream Stream => this.stream ??= new CountingWriteStream(inner.Stream, counter);

    public PipeWriter Writer => this.writer ??= new CountingPipeWriter(inner.Writer, counter);

    public ValueTask StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);

    public ValueTask CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);
}

sealed class CountingWriteStream(Stream inner, RequestCounter counter) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
        => this.Write(new ReadOnlySpan<byte>(buffer, offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        counter.AddOut(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => this.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        counter.AddOut(buffer.Length);
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // The connection owns its own producer and completes it itself; disposing the inner stream from
    // here would end the response the moment anything wrapping it went out of scope.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);
}

/// <summary>
/// Counts without copying. Buffers are handed out by the inner writer and written in place, so the
/// only thing this adds to the hot path is an add on <see cref="Advance"/>.
/// </summary>
sealed class CountingPipeWriter(PipeWriter inner, RequestCounter counter) : PipeWriter
{
    public override void Advance(int bytes)
    {
        counter.AddOut(bytes);
        inner.Advance(bytes);
    }

    public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

    // Counted here rather than left to the base implementation, which would route through GetSpan
    // and Advance and lose whatever the inner writer does better with a whole buffer in hand.
    public override ValueTask<FlushResult> WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        counter.AddOut(source.Length);
        return inner.WriteAsync(source, cancellationToken);
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        => inner.FlushAsync(cancellationToken);

    public override void CancelPendingFlush() => inner.CancelPendingFlush();

    public override void Complete(Exception? exception = null) => inner.Complete(exception);

    public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);

    public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;

    public override long UnflushedBytes => inner.UnflushedBytes;
}
