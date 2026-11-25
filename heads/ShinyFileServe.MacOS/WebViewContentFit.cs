using System.Runtime.CompilerServices;
using AppKit;
using Foundation;
using WebKit;

namespace ShinyFileServe.MacOS;

/// <summary>
/// Keeps WebKit's own content view the size of the web view. The maui-labs handler creates the
/// WKWebView with an empty frame and lays it out afterwards, and since preview.15 that layout no
/// longer reaches the view WebKit paints into: the web view is 1180x748, the page loads, runs and
/// renders - and its content view stays 0x0, so the window shows nothing but the web view's white
/// background. Sizing it ourselves whenever the web view's frame changes puts the page back.
/// </summary>
static class WebViewContentFit
{
    // One observer per web view, released with it. A closed console window is replaced by a new
    // one with a new web view, so this has to follow the view rather than live once for the app.
    static readonly ConditionalWeakTable<WKWebView, NSObject> Observers = new();

    public static void Attach(WKWebView web)
    {
        if (Observers.TryGetValue(web, out _))
            return;

        web.PostsFrameChangedNotifications = true;
        var observer = NSNotificationCenter.DefaultCenter.AddObserver(
            NSView.FrameChangedNotification,
            _ => Fit(web),
            web
        );
        Observers.Add(web, observer);
        Fit(web);
    }

    static void Fit(WKWebView web)
    {
        var bounds = web.Bounds;
        foreach (var content in web.Subviews.Where(x => x.Class.Name == "WKFlippedView" && x.Frame != bounds))
            content.Frame = bounds;
    }
}
