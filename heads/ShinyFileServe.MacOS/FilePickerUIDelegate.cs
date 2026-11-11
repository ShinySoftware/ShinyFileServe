using AppKit;
using Foundation;
using WebKit;

namespace ShinyFileServe.MacOS;

/// <summary>
/// Gives the console window's web view a file picker. An AppKit WKWebView with no UI delegate
/// swallows every click on an &lt;input type="file"&gt; - no panel, no error - and the maui-labs
/// WebView handler sets none. That left choosing a backup at setup, restoring one from settings and
/// uploading files all doing nothing in the app's own window while working fine in a browser.
/// </summary>
public class FilePickerUIDelegate : WKUIDelegate
{
    public override void RunOpenPanel(
        WKWebView webView,
        WKOpenPanelParameters parameters,
        WKFrameInfo frame,
        Action<NSUrl[]> completionHandler
    )
    {
        var panel = NSOpenPanel.OpenPanel;
        panel.CanChooseFiles = true;
        panel.CanChooseDirectories = parameters.AllowsDirectories;
        panel.AllowsMultipleSelection = parameters.AllowsMultipleSelection;

        void Complete(nint result)
            => completionHandler(result == (nint)(long)NSModalResponse.OK ? panel.Urls : null!);

        // As a sheet on the window when there is one, so it reads as this window's question.
        if (webView.Window is { } window)
            panel.BeginSheet(window, Complete);
        else
            Complete(panel.RunModal());
    }
}
