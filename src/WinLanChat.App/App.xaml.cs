using System.Windows;
using WinLanChat.App.ViewModels;
using WinLanChat.App.Services;

namespace WinLanChat.App;

public partial class App : Application
{
    internal SystemTrayService? SystemTray { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var viewModel = new MainViewModel(Dispatcher, ProfileSettingsStore.ForCurrentUser(),
            startupService: new WindowsStartupService());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        SystemTray = new SystemTrayService(window, viewModel);
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
        base.OnExit(e);
    }
}
