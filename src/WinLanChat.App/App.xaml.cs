using System.Windows;
using WinLanChat.App.ViewModels;

namespace WinLanChat.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var viewModel = new MainViewModel(Dispatcher);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Closed += async (_, _) =>
        {
            try { await viewModel.DisposeAsync(); }
            finally { Shutdown(); }
        };
        window.Show();
    }
}
