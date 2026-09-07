using ShinyFileServe.App.Services;

namespace ShinyFileServe.App;

public partial class App : Application
{
    readonly IAppWindowController windows;

    public App(IAppWindowController windows)
    {
        this.InitializeComponent();
        this.windows = windows;
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => this.windows.CreateWindow();
}
