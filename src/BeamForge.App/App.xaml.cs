using System.Windows;
using System.Windows.Threading;

namespace BeamForge.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (MainWindow is MainWindow window) await window.EmergencyBlackoutAsync("Unhandled error; blackout requested.");
        e.Handled = false;
    }
}
