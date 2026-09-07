using Microsoft.AspNetCore.Components;

namespace ShinyFileServe.Web.Services;

/// <summary>
/// Ending a session. Two things do it - the button in the top bar and the idle timer - and they have
/// to agree on the order, so there is one implementation of it rather than one per caller.
/// </summary>
public class SessionActions(FileServeApi api, WorkspaceState state, IdleWatch idle, NavigationManager nav)
{
    /// <summary>
    /// <paramref name="wentIdle"/> only changes where the browser lands: coming back to a sign-in
    /// screen with no explanation reads as a bug rather than as a policy.
    /// </summary>
    public async Task SignOut(bool wentIdle = false)
    {
        try
        {
            await api.SignOut();
        }
        catch (ApiException)
        {
            // the cookie is gone either way; there is nothing useful to tell the user here
        }

        await idle.Stop();
        state.SetUser(null);

        // A full load rather than a routed navigation: the sign-in page comes up with nothing of the
        // previous session left in memory.
        nav.NavigateTo(wentIdle ? "signin?idle=1" : "signin", forceLoad: true);
    }
}
