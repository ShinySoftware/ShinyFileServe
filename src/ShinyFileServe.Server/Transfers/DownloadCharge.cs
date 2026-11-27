using System.Globalization;
using Shiny.Net.HttpServer;
using Shiny.Net.HttpServer.Files;

namespace ShinyFileServe.Server.Transfers;

/// <summary>
/// How many bytes a download request will actually send, worked out before the response is written
/// so the quota can be checked and charged with that number instead of the whole file.
/// </summary>
/// <remarks>
/// Charging the whole file for every request double-charged a resumed download (the resume asks for
/// a range) and charged a full file for every seek in a video. It also refused a resume whenever the
/// quota had less than a whole file left, so the browser gave up and the person started again from
/// zero. Charging the range is not a loophole: fetching a file in pieces still costs every byte once.
/// <para>
/// The rules are the ones <see cref="FileDownloadResult"/> applies when it writes the response, so
/// the charge always matches what goes on the wire: a 304 sends nothing, an unsatisfiable range is a
/// 416 that sends nothing, a stale <c>If-Range</c> or a multi-range request gets the whole file, and
/// one satisfiable range gets that range.
/// </para>
/// </remarks>
public static class DownloadCharge
{
    public static long For(HttpRequest request, long length, string etag, DateTimeOffset lastModified)
    {
        if (IsNotModified(request, etag, lastModified))
            return 0;

        // A stale If-Range means the client's partial copy is out of date: the whole file is sent.
        if (!RangeIsFresh(request, etag, lastModified))
            return length;

        if (RangeHeader.TryParse(request.Headers.GetFirst("Range"), length, out var range, out var unsatisfiable))
            return range.Length;

        // No usable range: the whole file, unless the range was well-formed but past the end, which is a 416.
        return unsatisfiable ? 0 : length;
    }

    static bool IsNotModified(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        var ifNoneMatch = request.Headers.GetFirst("If-None-Match");
        if (!String.IsNullOrEmpty(ifNoneMatch))
        {
            if (ifNoneMatch.Trim() == "*")
                return true;

            return ifNoneMatch
                .Split(',')
                .Select(x => x.Trim())
                .Select(x => x.StartsWith("W/", StringComparison.Ordinal) ? x[2..] : x)
                .Any(x => x == etag);
        }

        var ifModifiedSince = request.Headers.GetFirst("If-Modified-Since");
        return !String.IsNullOrEmpty(ifModifiedSince)
            && DateTimeOffset.TryParse(ifModifiedSince, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var since)
            && lastModified.ToUnixTimeSeconds() <= since.ToUnixTimeSeconds();
    }

    static bool RangeIsFresh(HttpRequest request, string etag, DateTimeOffset lastModified)
    {
        var ifRange = request.Headers.GetFirst("If-Range");
        if (String.IsNullOrEmpty(ifRange))
            return true;

        var value = ifRange.Trim();
        if (value.StartsWith('"') || value.StartsWith("W/\"", StringComparison.Ordinal))
            return value == etag;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var date)
            && lastModified.ToUnixTimeSeconds() == date.ToUnixTimeSeconds();
    }
}
