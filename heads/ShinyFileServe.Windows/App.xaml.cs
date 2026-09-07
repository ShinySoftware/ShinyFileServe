using Microsoft.UI.Xaml;

namespace ShinyFileServe.Windows;

public partial class WinApp : MauiWinUIApplication
{
    public WinApp() => this.InitializeComponent();

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
