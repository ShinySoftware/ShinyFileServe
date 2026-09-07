using Microsoft.JSInterop;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// Signs the browser out after a period with no input. Enforced here rather than on the server
/// because "the screen has gone idle" is something only the browser can see - the server cannot
/// tell an idle user from one who is reading.
/// </summary>
public class IdleWatch(IJSRuntime js) : IAsyncDisposable
{
    IJSObjectReference? module;
    DotNetObjectReference<IdleWatch>? self;
    Func<Task>? onIdle;

    public async Task Start(int idleMinutes, Func<Task> handler)
    {
        await this.Stop();

        if (idleMinutes <= 0)
            return;

        this.onIdle = handler;
        this.self = DotNetObjectReference.Create(this);
        this.module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/idle.js");
        await this.module.InvokeVoidAsync("start", this.self, idleMinutes);
    }

    public async Task Stop()
    {
        if (this.module is not null)
        {
            try
            {
                await this.module.InvokeVoidAsync("stop");
            }
            catch (JSDisconnectedException)
            {
                // the page is going away; there is nothing left to stop
            }
        }

        this.self?.Dispose();
        this.self = null;
        this.onIdle = null;
    }

    [JSInvokable]
    public Task OnIdle() => this.onIdle?.Invoke() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await this.Stop();

        if (this.module is not null)
        {
            try
            {
                await this.module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
        GC.SuppressFinalize(this);
    }
}
