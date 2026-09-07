using Microsoft.UI.Dispatching;
using WinUIApplication = Microsoft.UI.Xaml.Application;

namespace ShinyFileServe.Windows;

/// <summary>Unpackaged WinUI entry point - WindowsPackageType is None so there is no MSIX activation.</summary>
public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        WinUIApplication.Start(_p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new WinApp();
        });
    }
}
