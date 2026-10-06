using Shiny.Net.HttpServer;
using ShinyFileServe.Shared;

namespace ShinyFileServe.Server.PackageFeeds;

/// <summary>
/// Reads what a feed request was for out of its method and path, for the traffic log. Best effort:
/// a path this does not recognise is still logged, as metadata with no package named.
/// </summary>
static class PackageRequestParser
{
    public static (PackageOperation Operation, string? PackageId, string? Version) Parse(
        PackageFeed feed,
        string method,
        string path,
        string? published)
    {
        var (operation, id, version) = feed == PackageFeed.NuGet
            ? NuGet(method, Rest(path, PackageFeedHost.NuGetPrefix))
            : Npm(method, Rest(path, PackageFeedHost.NpmPrefix));

        // A push carries its package in the body, not the path; the feed library hands it over once
        // it has read it, and that is the better answer for any write that has one.
        if (published is { Length: > 0 } && operation == PackageOperation.Publish)
        {
            var separator = feed == PackageFeed.NuGet ? published.LastIndexOf(' ') : published.LastIndexOf('@');
            if (separator > 0)
                return (operation, published[..separator], published[(separator + 1)..]);
        }

        return (operation, id, version);
    }

    static string Rest(string path, string prefix)
        => path.Length > prefix.Length ? path[(prefix.Length + 1)..] : "";

    static (PackageOperation, string?, string?) NuGet(string method, string rest)
    {
        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (rest.StartsWith("api/v2/package", StringComparison.OrdinalIgnoreCase))
        {
            if (HttpMethods.IsPut(method))
                return (PackageOperation.Publish, null, null);

            return (PackageOperation.Remove, At(parts, 3), At(parts, 4));
        }

        if (rest.StartsWith("v3/search", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("v3/autocomplete", StringComparison.OrdinalIgnoreCase))
            return (PackageOperation.Search, null, null);

        // v3/package/{id}/index.json, or v3/package/{id}/{version}/{file}.
        if (parts.Length >= 3 && parts[0].Equals("v3", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("package", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length >= 5)
            {
                var download = parts[4].EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase);
                return (download ? PackageOperation.Download : PackageOperation.Metadata, parts[2], parts[3]);
            }

            return (PackageOperation.Metadata, parts[2], null);
        }

        if (parts.Length >= 3 && parts[1].Equals("registration", StringComparison.OrdinalIgnoreCase))
            return (PackageOperation.Metadata, parts[2], null);

        return (PackageOperation.Metadata, null, null);
    }

    static (PackageOperation, string?, string?) Npm(string method, string rest)
    {
        var read = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

        if (rest is "" or "-/ping"
            || rest.StartsWith("-/whoami", StringComparison.Ordinal)
            || rest.StartsWith("-/user/", StringComparison.Ordinal)
            || rest.StartsWith("-/npm/v1/security/", StringComparison.Ordinal))
            return (PackageOperation.Account, null, null);

        if (rest.StartsWith("-/v1/search", StringComparison.Ordinal))
            return (PackageOperation.Search, null, null);

        if (rest.StartsWith("-/package/", StringComparison.Ordinal))
        {
            var (tagged, _) = Name(rest["-/package/".Length..]);
            return (read ? PackageOperation.Metadata : PackageOperation.Publish, tagged, null);
        }

        var (name, after) = Name(rest);
        if (name is null)
            return (PackageOperation.Metadata, null, null);

        // {name}/-/{unscoped}-{version}.tgz
        if (after.StartsWith("-/", StringComparison.Ordinal) && after.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            var file = after[2..^4];
            var unscoped = name.Contains('/') ? name[(name.IndexOf('/') + 1)..] : name;
            var version = file.StartsWith(unscoped + "-", StringComparison.Ordinal) ? file[(unscoped.Length + 1)..] : null;
            return (PackageOperation.Download, name, version);
        }

        if (HttpMethods.IsDelete(method))
            return (PackageOperation.Remove, name, null);

        if (!read)
            return (PackageOperation.Publish, name, null);

        return (PackageOperation.Metadata, name, after.Length > 0 && !after.Contains('/') ? after : null);
    }

    /// <summary>
    /// The package name at the front of a path and whatever follows it. A scoped name is two segments,
    /// and npm sends the slash between them escaped.
    /// </summary>
    static (string? Name, string After) Name(string rest)
    {
        var decoded = Uri.UnescapeDataString(rest);
        var segments = decoded.Split('/');
        if (segments.Length == 0 || segments[0].Length == 0)
            return (null, "");

        var take = segments[0].StartsWith('@') && segments.Length > 1 ? 2 : 1;
        var name = String.Join('/', segments[..take]);
        var after = String.Join('/', segments[take..]);
        return (name, after);
    }

    static string? At(string[] parts, int index) => parts.Length > index ? parts[index] : null;
}
