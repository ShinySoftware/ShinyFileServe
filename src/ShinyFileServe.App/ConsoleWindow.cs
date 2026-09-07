using Microsoft.Extensions.DependencyInjection;
using ShinyFileServe.App.Services;

namespace ShinyFileServe.App;

/// <summary>
/// The whole desktop UI: a browser onto the app's own server.
/// </summary>
/// <remarks>
/// <para>
/// There are deliberately no native screens. Everything an operator does - users, groups, shares,
/// quotas, settings, the activity log - is in the portal, and building the same forms twice would
/// have meant two of everything to keep in step and one of them quietly falling behind.
/// </para>
/// <para>
/// A request from this machine is signed in without a password, so the window opens straight into
/// the portal rather than onto a login form the person running the server has to fill in to see
/// their own screen.
/// </para>
/// </remarks>
public class ConsoleWindowPage : ContentPage
{
    readonly ServerController server;
    readonly WebView web = new();
    readonly Label status = new()
    {
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Color.FromArgb("#5D6874"),
        FontSize = 14
    };
    readonly Grid placeholder;

    string? loaded;

    public ConsoleWindowPage(ServerController server)
    {
        this.server = server;
        this.web.Navigating += this.OnNavigating;
        this.Title = "FileServe";
        this.BackgroundColor = Color.FromArgb("#F1F4F6");
        this.Padding = 0;

        this.placeholder = new Grid
        {
            BackgroundColor = Color.FromArgb("#F1F4F6"),
            Padding = 32,
            Children =
            {
                new VerticalStackLayout
                {
                    Spacing = 14,
                    VerticalOptions = LayoutOptions.Center,
                    HorizontalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new BoxView
                        {
                            WidthRequest = 30,
                            HeightRequest = 30,
                            CornerRadius = 7,
                            Color = Color.FromArgb("#0E6F70"),
                            HorizontalOptions = LayoutOptions.Center
                        },
                        this.status
                    }
                }
            }
        };

        this.Content = new Grid { Children = { this.web, this.placeholder } };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.server.Changed += this.OnServerChanged;
        this.Refresh();

        // Idempotent, and the tray already called it - this is only here for the case where a
        // window is reopened long after startup and the container was rebuilt underneath it.
        _ = this.server.Initialize();
    }

    protected override void OnDisappearing()
    {
        this.server.Changed -= this.OnServerChanged;
        base.OnDisappearing();
    }

    void OnServerChanged(object? sender, EventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.IsDispatchRequired)
            this.Refresh();
        else
            dispatcher.Dispatch(this.Refresh);
    }

    void Refresh()
    {
        // Always loopback, never the LAN address: this is the one connection that gets signed in
        // without a password, and that only holds for a request that never left the machine.
        var url = this.server.IsServing ? this.server.LoopbackUrl : null;

        if (url is null)
        {
            this.status.Text = this.server.LastError is { Length: > 0 } error
                ? $"The server is not running.\n{error}"
                : "The server is not running. Start it from the FileServe icon in the menu bar.";

            this.placeholder.IsVisible = true;
            this.web.IsVisible = false;
            this.loaded = null;
            return;
        }

        this.placeholder.IsVisible = false;
        this.web.IsVisible = true;

        // Only navigate when the address actually changed - a reload on every state change would
        // throw away whatever the operator was in the middle of.
        if (this.loaded != url)
        {
            this.loaded = url;
            this.web.Source = url;
        }
    }

    /// <summary>
    /// A download must not become a navigation. The web view renders a PDF itself - and an image, and
    /// a text file - whatever Content-Disposition says, which replaces the portal with a viewer this
    /// window has no way back from: there is no address bar and no back button here. So anything
    /// under /api is handed to the machine's own browser instead, which is also what puts the file
    /// where the person expects to find it. A request from this machine is signed in without a
    /// password, so it arrives there as the same person.
    /// </summary>
    /// <remarks>
    /// Only navigations reach this. Everything the portal does for itself goes over fetch, which is
    /// not a navigation and never lands here - so this sees exactly the download links and nothing
    /// else.
    /// </remarks>
    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!IsFileTransfer(e.Url))
            return;

        e.Cancel = true;
        SystemBrowser.Open(e.Url);
    }

    static bool IsFileTransfer(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
}
