using Shiny.AppDeviceBridge;

namespace ShinyFileServe.Server;

/// <summary>
/// The bridge server's allowed host names, kept in step with the settings. The bridge reads its set
/// on every request, so changing the contents is all it takes for a saved name to start working -
/// no restart, the same as the remember-me window.
/// </summary>
public class AllowedHostsHolder
{
    readonly Lock gate = new();
    AppDeviceBridgeOptions? bridge;
    IReadOnlyList<string> always = [];
    IReadOnlyList<string> configured = [];

    /// <summary>
    /// Handed the live options by the registration, with the names that are always allowed. Names
    /// applied before this - the settings can be read before the bridge is built - land now.
    /// </summary>
    internal void Attach(AppDeviceBridgeOptions options, IEnumerable<string> alwaysAllowed)
    {
        lock (this.gate)
        {
            this.bridge = options;
            this.always = [.. alwaysAllowed];
            this.Sync();
        }
    }

    /// <summary>Replaces the names that came from the settings. This machine's own names stay.</summary>
    public void Apply(IEnumerable<string> names)
    {
        lock (this.gate)
        {
            this.configured = [.. names];
            this.Sync();
        }
    }

    void Sync()
    {
        if (this.bridge is not { } options)
            return;

        var wanted = new HashSet<string>(this.always, StringComparer.OrdinalIgnoreCase);
        wanted.UnionWith(this.configured);

        // Removed and added one at a time rather than cleared and refilled, so a request checked
        // part-way through still finds every name that was allowed before and still is.
        foreach (var stale in options.AllowedHosts.Where(x => !wanted.Contains(x)).ToList())
            options.AllowedHosts.Remove(stale);

        foreach (var name in wanted)
            options.AllowedHosts.Add(name);
    }

    /// <summary>
    /// The host part of whatever was typed, lower-case and in its DNS (punycode) form: people paste
    /// <c>https://files.example.com/</c> as often as they type <c>files.example.com</c>, and a port
    /// means nothing here because the check is on the name alone. Null when there is no DNS name in
    /// it - an IP address included, since those are always allowed and listing one would mislead.
    /// </summary>
    public static string? Normalize(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
            return null;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.Length == 0 ? null : host;
    }
}
