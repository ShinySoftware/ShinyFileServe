namespace ShinyFileServe.Shared;

/// <summary>
/// What everyone is told about a pinggy tunnel opened without an access token, in one place so the
/// portal, the tray and the server log say the same thing.
/// </summary>
/// <remarks>
/// An anonymous pinggy tunnel lasts 60 minutes. When it ends it reconnects under a new random
/// address, so a download or upload in progress through it is cut off with nothing to resume
/// against, and every link that was handed out stops working. That is easy to mistake for a fault in
/// the server, so the person opening the tunnel is told up front.
/// </remarks>
public static class TunnelWarnings
{
    public const string AnonymousTitle = "Anonymous tunnel: it will close within an hour";

    public const string Anonymous =
        "Without a pinggy access token, the tunnel closes after 60 minutes and comes back at a new address. " +
        "Downloads and uploads running through it are cut off, and links to the old address stop working. " +
        "Add a pinggy token in Settings for a tunnel that stays up at one address.";

    public const string ReconnectedTitle = "The anonymous tunnel moved to a new address";

    public static string Reconnected(string? url)
        => $"The old address no longer works, and anything being transferred through it was cut off. " +
           $"The tunnel is now at {url ?? "a new address"}. Add a pinggy token in Settings to keep one address.";
}
