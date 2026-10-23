using System.Windows;
using WinLanChat.App.ViewModels;
using WinLanChat.App.Services;

namespace WinLanChat.App;

public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    internal SystemTrayService? SystemTray { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        _singleInstance = SingleInstanceService.TryAcquire();
        if (_singleInstance is null)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var viewModel = new MainViewModel(Dispatcher, ProfileSettingsStore.ForCurrentUser(),
            startupService: new WindowsStartupService());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        SystemTray = new SystemTrayService(window, viewModel);
        _singleInstance.ListenForActivation(() =>
            Dispatcher.InvokeAsync(() => SystemTray?.RestoreWindow()));
        window.Closed += async (_, _) =>
        {
            try { await viewModel.DisposeAsync(); }
            finally { Shutdown(); }
        };
        window.Show();
        await viewModel.InitializeAsync();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logoff/shutdown must not be cancelled by close-to-tray behavior.
        SystemTray?.Dispose();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemTray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
