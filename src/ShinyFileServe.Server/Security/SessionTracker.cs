using System.Security.Claims;
using Shiny.Net.HttpServer;

namespace ShinyFileServe.Server.Security;

public record ActiveSession(string UserId, string Username, DateTimeOffset LastSeenUtc);

/// <summary>
/// Who is actually using the server right now. There is no server-side session store - the cookie
/// carries its own claims - so "active" is inferred from requests: a person who has asked for
/// something inside the window is here, and one who has not has gone.
/// </summary>
public interface ISessionTracker
{
    /// <summary>People using the server from elsewhere. The machine's own window is not a session.</summary>
    int ActiveCount { get; }

    IReadOnlyList<ActiveSession> Active { get; }

    /// <summary>Raised when the count changes, from a request thread or the sweep timer.</summary>
    event EventHandler? Changed;

    void Touch(string userId, string username, bool isLocal);

    /// <summary>Forgets everyone. Used when the listener stops - nobody is connected to a closed socket.</summary>
    void Clear();
}

public class SessionTracker : ISessionTracker, IDisposable
{
    /// <summary>
    /// How long after someone's last request they still count as here. Long enough to cover reading
    /// a page without clicking, short enough that the tray goes quiet soon after they leave.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    readonly Dictionary<string, ActiveSession> sessions = [];
    readonly Lock gate = new();
    readonly TimeProvider time;
    readonly ITimer sweep;

    int lastCount;

    public SessionTracker(TimeProvider time)
    {
        this.time = time;

        // Requests are what add someone; only a clock can take them away again, so the count is
        // re-checked on a timer rather than left stale until the next request happens to arrive.
        this.sweep = time.CreateTimer(
            _ => this.RaiseIfChanged(),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30)
        );
    }

    public event EventHandler? Changed;

    public int ActiveCount => this.Snapshot().Count;

    public IReadOnlyList<ActiveSession> Active => this.Snapshot();

    public void Touch(string userId, string username, bool isLocal)
    {
        // The console window on this machine is signed in without a password and is always there.
        // Counting it would leave the tray permanently lit and tell the operator nothing.
        if (isLocal || String.IsNullOrEmpty(userId))
            return;

        lock (this.gate)
            this.sessions[userId] = new ActiveSession(userId, username, this.time.GetUtcNow());

        this.RaiseIfChanged();
    }

    public void Clear()
    {
        lock (this.gate)
            this.sessions.Clear();

        this.RaiseIfChanged();
    }

    List<ActiveSession> Snapshot()
    {
        var cutoff = this.time.GetUtcNow() - Window;

        lock (this.gate)
        {
            foreach (var stale in this.sessions.Where(x => x.Value.LastSeenUtc < cutoff).Select(x => x.Key).ToList())
                this.sessions.Remove(stale);

            return this.sessions.Values.OrderBy(x => x.Username).ToList();
        }
    }

    void RaiseIfChanged()
    {
        var count = this.Snapshot().Count;
        if (Interlocked.Exchange(ref this.lastCount, count) == count)
            return;

        this.Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        this.sweep?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Records that the caller is here. Sits after authentication so it sees who they are, and after
/// the loopback middleware so it can tell the machine's own window from a real visitor.
/// </summary>
public class SessionTrackingMiddleware(ISessionTracker tracker) : IHttpMiddleware
{
    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.User.Identity is { IsAuthenticated: true })
        {
            var id = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (id is { Length: > 0 })
            {
                tracker.Touch(
                    id,
                    context.User.Identity.Name ?? "",
                    context.User.HasClaim(PrincipalFactory.LocalSessionClaim, "true")
                );
            }
        }

        return next(context);
    }
}
