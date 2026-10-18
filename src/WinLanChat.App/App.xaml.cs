using System.Windows;
using WinLanChat.App.ViewModels;
using WinLanChat.App.Services;

namespace WinLanChat.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var viewModel = new MainViewModel(Dispatcher, ProfileSettingsStore.ForCurrentUser(),
            startupService: new WindowsStartupService());
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Closed += async (_, _) =>
        {
            try { await viewModel.DisposeAsync(); }
            finally { Shutdown(); }
        };
        window.Show();
        await viewModel.InitializeAsync();
    }
}
