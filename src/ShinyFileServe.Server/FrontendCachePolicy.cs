using Shiny.Net.HttpServer.StaticFiles;

namespace ShinyFileServe.Server;

/// <summary>
/// Cache headers for the published frontend.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the default Blazor policy, which caches everything under <c>_framework/</c> for a year
/// as immutable. Most of what is in there is content-fingerprinted and safe to treat that way, but
/// two files are not: <c>dotnet.js</c> and <c>blazor.webassembly.js</c> keep the same name across
/// builds, and <c>dotnet.js</c> is the file that names every fingerprinted asset. Cached for a
/// year, it pins a browser to the build it first loaded - the assemblies change name, and nothing
/// ever asks for the new ones. The desktop window has its own persistent cache, so this is not
/// hypothetical: an upgraded app opens on the previous version's UI.
/// </para>
/// <para>
/// So immutability is decided by the filename rather than the directory, and everything else
/// revalidates. Revalidating is nearly free - the static handler answers a conditional request
/// with a 304 and no body.
/// </para>
/// </remarks>
public static class FrontendCachePolicy
{
    public static void Apply(StaticFileResponseContext context)
    {
        var headers = context.HttpContext.Response.Headers;

        headers["Cache-Control"] = IsFingerprinted(context.File.Name)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }

    /// <summary>
    /// Whether a publish gave this file a content hash in its name, as in
    /// <c>dotnet.runtime.zbexyp8zrs.js</c> - in which case a change to it is a change of URL and
    /// the old one can never go stale.
    /// </summary>
    /// <remarks>
    /// The digit is what separates a hash from an ordinary name part: <c>dotnet.native.x.js</c> is
    /// fingerprinted, <c>blazor.webassembly.js</c> is not, and "webassembly" is the right length to
    /// be mistaken for a hash without it. Guessing wrong in this direction only costs a 304;
    /// guessing wrong the other way is the bug this exists to avoid.
    /// </remarks>
    internal static bool IsFingerprinted(string fileName)
    {
        var parts = fileName.Split('.');
        if (parts.Length < 3)
            return false;

        var candidate = parts[^2];
        return candidate.Length is >= 8 and <= 12
            && candidate.All(Char.IsAsciiLetterOrDigit)
            && candidate.Any(Char.IsAsciiDigit)
            && !candidate.Any(Char.IsAsciiLetterUpper);
    }
}
