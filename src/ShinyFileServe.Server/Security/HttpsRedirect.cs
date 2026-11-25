using Shiny.Net.HttpServer;

namespace ShinyFileServe.Server.Security;

/// <summary>
/// Sends a browser that opened the portal over plain http from another machine to the HTTPS
/// listener instead, where the portal can actually run.
///
/// Deliberately narrow. Only page loads are moved - a GET or HEAD that asks for HTML outside the API
/// and the package feeds - because an API call, a NuGet restore or an npm install has no person to
/// accept a self-signed certificate. And nothing is moved that did not come straight from a browser:
/// this machine's own window (loopback is already secure), a tunnel (already https), and anything a
/// reverse proxy forwarded, which says so in X-Forwarded-*. A proxy terminates TLS itself and
/// forwards plain http on purpose; sending its users to this server's own port would go around it.
/// </summary>
public class HttpsRedirectMiddleware(ServerTls tls) : IHttpMiddleware
{
    public ValueTask InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (tls.ActivePort is { } port && ShouldMove(context) && Target(context, port) is { } location)
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers["Location"] = location;
            context.Response.Headers["Cache-Control"] = "no-store";
            return ValueTask.CompletedTask;
        }

        return next(context);
    }

    static bool ShouldMove(HttpContext ctx)
    {
        var request = ctx.Request;
        if (RequestScheme.IsHttps(ctx) || RequestScheme.IsForwarded(ctx) || ctx.Connection.IsTunneled)
            return false;

        if (LoopbackSignInMiddleware.IsLocal(ctx))
            return false;

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            return false;

        var path = request.Path.ToString();
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || PackageFeeds.PackageFeedHost.Classify(path) != PackageFeeds.PackageFeed.None)
            return false;

        var accept = request.Headers.GetFirst("Accept");
        return accept is not null && accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    static string? Target(HttpContext ctx, int port)
    {
        var authority = ctx.Request.Host;
        if (String.IsNullOrEmpty(authority) || !Uri.TryCreate($"http://{authority}", UriKind.Absolute, out var asked))
            return null;

        var builder = new UriBuilder(Uri.UriSchemeHttps, asked.Host, port);
        var path = ctx.Request.Path.ToString();
        // Null when the address has no query at all.
        var query = ctx.Request.QueryString ?? String.Empty;
        return $"{builder.Uri.GetLeftPart(UriPartial.Authority)}{path}{query}";
    }
}

/// <summary>What the browser was really talking to - this server, or a reverse proxy in front of it.</summary>
public static class RequestScheme
{
    /// <summary>
    /// https on this listener, or https at a proxy that says so. Only ever used to write links and to
    /// decide against a redirect, so a client that lies about it only hurts itself.
    /// </summary>
    public static bool IsHttps(HttpContext ctx)
        => String.Equals(ctx.Request.Scheme, "https", StringComparison.OrdinalIgnoreCase)
            || String.Equals(ForwardedProto(ctx), "https", StringComparison.OrdinalIgnoreCase);

    public static string Of(HttpContext ctx) => IsHttps(ctx) ? "https" : "http";

    /// <summary>A reverse proxy forwarded this request.</summary>
    public static bool IsForwarded(HttpContext ctx)
        => ctx.Request.Headers.GetFirst("X-Forwarded-Proto") is not null
            || ctx.Request.Headers.GetFirst("X-Forwarded-For") is not null;

    // The first hop is the one the browser spoke to.
    static string? ForwardedProto(HttpContext ctx)
        => ctx.Request.Headers.GetFirst("X-Forwarded-Proto")?.Split(',')[0].Trim();
}
