using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Networking;

internal static class WindowsStartupChecks
{
    internal static async Task RegistryAsync()
    {
        const string executable = @"C:\Apps\LAN Chat\WinLanChat.App.exe";
        string command = WindowsStartupService.CreateLaunchCommand(executable, "");
        if (command != $"\"{executable}\"")
            throw new Exception("Executable paths with spaces must be quoted.");
        const string host = @"C:\Program Files\dotnet\dotnet.exe";
        const string assembly = @"C:\Apps\LAN Chat\WinLanChat.App.dll";
        if (WindowsStartupService.CreateLaunchCommand(host, assembly) != $"\"{host}\" \"{assembly}\"")
            throw new Exception("A dotnet-hosted app must register both the host and its assembly.");

        // Exercise real registry persistence without touching the user's startup registration.
        string path = @"Software\WinLanChat.Tests\" + Guid.NewGuid().ToString("N");
        var service = new WindowsStartupService(path, command);
        try
        {
            if (await service.IsEnabledAsync())
                throw new Exception("A missing registration should be disabled.");
            await service.SetEnabledAsync(false);
            using (var missing = Registry.CurrentUser.OpenSubKey(path))
                if (missing is not null)
                    throw new Exception("Disabling startup created a registry key.");
            await service.SetEnabledAsync(true);
            await service.SetEnabledAsync(true);
            if (!await new WindowsStartupService(path, command).IsEnabledAsync())
                throw new Exception("A new service instance did not recover enabled startup.");
            using (var key = Registry.CurrentUser.OpenSubKey(path, writable: true)!)
            {
                if ((string?)key.GetValue("WinLanChat") != command)
                    throw new Exception("The startup command was not stored intact.");
                key.SetValue("OtherApp", "preserve");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await service.SetEnabledAsync(false, cancelled.Token);
                throw new Exception("A cancelled change succeeded.");
            }
            catch (OperationCanceledException) { }
            if (!await service.IsEnabledAsync())
                throw new Exception("A cancelled change removed the registration.");
            await service.SetEnabledAsync(false);
            await service.SetEnabledAsync(false);
            if (await service.IsEnabledAsync())
                throw new Exception("Disabling startup did not persist.");
            using var remaining = Registry.CurrentUser.OpenSubKey(path)!;
            if ((string?)remaining.GetValue("OtherApp") != "preserve")
                throw new Exception("Disabling startup affected another value.");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
    }

    internal static async Task UiAsync(Dispatcher dispatcher)
    {
        var service = new FakeStartupService { Enabled = true };
        await using var viewModel = CreateViewModel(dispatcher, service);
        if (viewModel.ToggleWindowsStartupCommand.CanExecute(null))
            throw new Exception("Startup can be changed before its state is loaded.");
        await viewModel.InitializeAsync();
        if (!viewModel.StartWithWindows || service.Writes != 0)
            throw new Exception("Initialization failed to load startup or rewrote it without consent.");
        var window = new WinLanChat.App.MainWindow { DataContext = viewModel };
        var checkbox = (CheckBox)window.FindName("WindowsStartupCheckBox");
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
        if (checkbox.IsChecked != true || !checkbox.IsEnabled)
            throw new Exception("The startup checkbox did not bind to the saved state.");

        await using var peer = new ChatNode("Startup preference test");
        peer.Start(0);
        await peer.ConnectAsync("127.0.0.1", int.Parse(viewModel.LocalPort));
        await UntilAsync(() => viewModel.IsConnected);
        service.PendingWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Click(checkbox);
        if (viewModel.ToggleWindowsStartupCommand.CanExecute(null))
            throw new Exception("Overlapping startup writes were allowed.");
        viewModel.ToggleWindowsStartupCommand.Execute(null);
        service.PendingWrite.SetResult();
        await UntilAsync(() => viewModel.ToggleWindowsStartupCommand.CanExecute(null));
        if (viewModel.StartWithWindows || checkbox.IsChecked != false || service.Enabled || service.Writes != 1)
            throw new Exception("Unchecking did not persist immediately during a conversation.");
        await peer.SendAsync("Still connected after changing startup");
        await UntilAsync(() => viewModel.Messages.Any(m => m.Text == "Still connected after changing startup"));

        service.FailWrite = true;
        Click(checkbox);
        await UntilAsync(() => viewModel.ToggleWindowsStartupCommand.CanExecute(null));
        if (!viewModel.HasError || viewModel.StartWithWindows || checkbox.IsChecked != false || service.Enabled)
            throw new Exception("A failed write did not report an error and restore the checkbox.");
        service.FailWrite = false;
        Click(checkbox);
        await UntilAsync(() => viewModel.ToggleWindowsStartupCommand.CanExecute(null));
        if (!viewModel.StartWithWindows || checkbox.IsChecked != true || !service.Enabled)
            throw new Exception("Retrying a startup write failed.");
        await viewModel.DisposeAsync();
        if (viewModel.ToggleWindowsStartupCommand.CanExecute(null))
            throw new Exception("A closed view model still permits registry writes.");
        await using var restarted = CreateViewModel(dispatcher, service);
        await restarted.InitializeAsync();
        if (!restarted.StartWithWindows)
            throw new Exception("A restarted view model lost the startup state.");
        await restarted.DisposeAsync();

        await using var unreadable = CreateViewModel(dispatcher, new FakeStartupService { FailRead = true });
        await unreadable.InitializeAsync();
        if (!unreadable.IsListening || !unreadable.ErrorMessage.Contains("автозапуск"))
            throw new Exception("A registry read failure prevented networking or was not reported.");
    }

    private static MainViewModel CreateViewModel(Dispatcher dispatcher, IWindowsStartupService service)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new MainViewModel(dispatcher, startupService: service)
        {
            LocalPort = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(),
            IsSettingsOpen = true
        };
    }

    private static void Click(CheckBox checkbox) => typeof(ToggleButton)
        .GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(checkbox, null);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private sealed class FakeStartupService : IWindowsStartupService
    {
        public bool Enabled { get; set; }
        public bool FailRead { get; init; }
        public bool FailWrite { get; set; }
        public int Writes { get; private set; }
        public TaskCompletionSource? PendingWrite { get; set; }

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
            FailRead ? Task.FromException<bool>(new UnauthorizedAccessException("Registry unavailable")) : Task.FromResult(Enabled);

        public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (PendingWrite is { } pending)
                await pending.Task.WaitAsync(cancellationToken);
            if (FailWrite)
                throw new UnauthorizedAccessException("Registry unavailable");
            Enabled = enabled;
        }
    }
}
