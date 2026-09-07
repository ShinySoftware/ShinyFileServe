using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Shiny.Blazor.Controls;
using Shiny.Blazor.Controls.Dialogs;
using Shiny.Blazor.Controls.Docking;
using Shiny.Blazor.Controls.FileDrop;
using Shiny.Blazor.Controls.Splash;
using ShinyFileServe.Web;
using ShinyFileServe.Web.Panels;
using ShinyFileServe.Web.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API lives on the same origin as this app - the server that hands out the SPA is the server
// that answers it, so the session cookie rides along with no CORS or credentials configuration.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<FileServeApi>();
builder.Services.AddScoped<WorkspaceState>();
builder.Services.AddScoped<AdminApi>();
builder.Services.AddScoped<IdleWatch>();
builder.Services.AddScoped<SessionActions>();

// The splash is painted before this code exists; this is what lets managed code talk to it -
// a status line while the app works out who is here, and the dismissal when it knows.
builder.Services.AddShinySplashScreen();

builder.Services.AddShinyControls(cfg => cfg
    .ConfigureDialogs(o => o.DefaultAnimation = DialogAnimation.Zoom)
);

// Whole-window drag and drop for uploads. The caps here are a coarse first filter; the real
// decision - may this person write to this folder, and is there quota left - is made per drop
// against the folder they are actually looking at.
builder.Services.AddShinyFileDrop(o =>
{
    o.MaxFiles = 50;
    o.MaxFileSize = 4L * 1024 * 1024 * 1024;
});

// Both panels are load-bearing: without the tree the file pane is a listing of nowhere, and
// without the file pane the tree selects into nothing. Neither gets a close button.
builder.Services.AddDockPanel<FolderTreePanel>("explorer-tree", displayName: "Folders", icon: "▤", canClose: false);
builder.Services.AddDockPanel<FilePanel>("explorer-files", displayName: "Files", icon: "▦", canClose: false);

await builder.Build().RunAsync();
