using Microsoft.JSInterop;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// Puts text on the clipboard over plain http as well as https. The browser's own API is missing on
/// a LAN address, which is where this server is usually reached, so the script falls back to the
/// older copy command rather than failing.
/// </summary>
public class Clipboard(IJSRuntime js) : IAsyncDisposable
{
    IJSObjectReference? module;

    /// <summary>False when the browser would not copy at all; the caller leaves the text on screen to select.</summary>
    public async Task<bool> Copy(string text)
    {
        try
        {
            this.module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            return await this.module.InvokeAsync<bool>("copy", text);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.module is not null)
        {
            try { await this.module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
