using Microsoft.UI.Xaml;
using Orbitale.App.Services;

namespace Orbitale.App;

public partial class App : Application
{
    Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Diagnostics.Log($"Unhandled: {e.Exception}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Diagnostics.Log("Launched");
        _window = new MainWindow();
        _window.Activate();
    }
}
