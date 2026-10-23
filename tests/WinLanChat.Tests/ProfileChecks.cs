using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Threading;
using WinLanChat.App.Models;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Models;
using WinLanChat.Core.Networking;

internal static class ProfileChecks
{
    internal static async Task PersistenceAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"WinLanChat-settings-{Guid.NewGuid():N}.json");
        var store = new ProfileSettingsStore(path);
        try
        {
            if (await store.LoadAsync() is not null)
                throw new Exception("A missing settings file did not use defaults.");
            var settings = new ProfileSettings("Рабочий компьютер 👋", 45833);
            await store.SaveAsync(settings);
            if (await store.LoadAsync() != settings)
                throw new Exception("Settings were not preserved across store instances.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await store.SaveAsync(settings with { DisplayName = "Cancelled" }, cancelled.Token);
                throw new Exception("A cancelled settings write succeeded.");
            }
            catch (OperationCanceledException) { }
            if (await store.LoadAsync() != settings)
                throw new Exception("A cancelled write damaged the saved settings.");
            await store.SaveAsync(settings with { LanguageCode = "en" });
            if ((await store.LoadAsync())?.LanguageCode != "en")
                throw new Exception("The selected language was not saved.");
            await File.WriteAllTextAsync(path, "{\"DisplayName\":\"Legacy profile\",\"ListeningPort\":45832}");
            if ((await store.LoadAsync())?.LanguageCode != "ru")
                throw new Exception("Existing profiles did not keep Russian as the default language.");
            foreach (string invalid in new[] { "{invalid json", "null", "{\"DisplayName\":null,\"ListeningPort\":45832}",
                "{\"DisplayName\":\"Name\",\"ListeningPort\":45832,\"LanguageCode\":\"de\"}",
                "{\"DisplayName\":\"Name\",\"ListeningPort\":45832,\"LanguageCode\":null}",
                "{\"DisplayName\":\"Name\",\"ListeningPort\":0}", "{\"DisplayName\":\"bad\\nname\",\"ListeningPort\":45832}" })
            {
                await File.WriteAllTextAsync(path, invalid);
                try
                {
                    await store.LoadAsync();
                    throw new Exception("Invalid saved settings were accepted.");
                }
                catch (Exception ex) when (ex is JsonException or ArgumentException) { }
            }
        }
        finally { File.Delete(path); }
    }

    internal static async Task RunAsync(Dispatcher dispatcher)
    {
        string path = Path.Combine(Path.GetTempPath(), $"WinLanChat-profile-{Guid.NewGuid():N}.json");
        var store = new ProfileSettingsStore(path);
        try
        {
            int originalPort = FreePort();
            var original = new ProfileSettings("Исходное имя", originalPort);
            await store.SaveAsync(original);
            IReadOnlyList<DiscoveredPeer> peers = [];
            await using var observer = new PeerDiscovery("Settings observer", 1);
            observer.PeersChanged += snapshot => Volatile.Write(ref peers, snapshot);
            observer.Start();
            await using var viewModel = new MainViewModel(dispatcher, store);
            await viewModel.InitializeAsync();
            if (!viewModel.IsListening || viewModel.DisplayName != original.DisplayName || viewModel.LocalPort != originalPort.ToString())
                throw new Exception("Automatic startup did not load saved settings.");
            await UntilAsync(() => Volatile.Read(ref peers).Any(p => p.Port == originalPort && p.Name == original.DisplayName));

            // A draft must affect neither the handshake nor the current conversation.
            viewModel.DisplayName = "Ещё не применено";
            await using (var peer = new ChatNode("Собеседник"))
            {
                string? advertisedName = null;
                peer.ConnectionChanged += info => { if (info is not null) advertisedName = info.Name; };
                peer.Start(0);
                await peer.ConnectAsync("127.0.0.1", originalPort);
                await UntilAsync(() => viewModel.IsConnected);
                if (advertisedName != original.DisplayName || !viewModel.CanEditProfile || viewModel.ApplyProfileCommand.CanExecute(null))
                    throw new Exception("A draft changed the live identity or allowed interruption of a conversation.");
                await peer.SendAsync("Разговор продолжается");
                await UntilAsync(() => viewModel.Messages.Any(m => m.Text == "Разговор продолжается"));
            }
            await UntilAsync(() => !viewModel.IsConnected);

            foreach (string invalidPort in new[] { "0", "65536", "abc" })
            {
                viewModel.LocalPort = invalidPort;
                viewModel.ApplyProfileCommand.Execute(null);
                if (!viewModel.HasError || !viewModel.IsListening || viewModel.ActiveDisplayName != original.DisplayName)
                    throw new Exception("An invalid port interrupted the working listener.");
            }
            viewModel.LocalPort = originalPort.ToString();
            viewModel.DisplayName = "bad\nname";
            viewModel.ApplyProfileCommand.Execute(null);
            if (!viewModel.HasError || !viewModel.IsListening)
                throw new Exception("An invalid name interrupted the working listener.");
            viewModel.ResetProfileCommand.Execute(null);
            if (viewModel.HasProfileChanges || viewModel.DisplayName != original.DisplayName)
                throw new Exception("Cancelling edits did not restore the active values.");

            // Renaming on the same port updates both discovery and the wire handshake.
            viewModel.DisplayName = "Новое имя";
            await ApplyAsync(viewModel);
            var renamed = original with { DisplayName = "Новое имя" };
            if (viewModel.HasError || await store.LoadAsync() != renamed)
                throw new Exception("Applying a name on the same port failed.");
            await observer.RefreshAsync();
            await UntilAsync(() => Volatile.Read(ref peers).Any(p => p.Port == originalPort && p.Name == renamed.DisplayName)
                && !Volatile.Read(ref peers).Any(p => p.Port == originalPort && p.Name == original.DisplayName));

            using var occupied = new TcpListener(IPAddress.Any, 0);
            occupied.Start();
            int newPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            viewModel.LocalPort = newPort.ToString();
            await ApplyAsync(viewModel);
            if (!viewModel.HasError || !viewModel.IsListening || !viewModel.LocalAddresses.Contains(originalPort.ToString())
                || await store.LoadAsync() != renamed)
                throw new Exception("A busy new port did not restore the previous endpoint and saved settings.");

            occupied.Stop();
            await ApplyAsync(viewModel);
            var changed = renamed with { ListeningPort = newPort };
            if (viewModel.HasError || await store.LoadAsync() != changed)
                throw new Exception("Applying a new listening port failed.");
            using (var released = new TcpListener(IPAddress.Any, originalPort))
                released.Start();
            await observer.RefreshAsync();
            await UntilAsync(() => Volatile.Read(ref peers).Any(p => p.Port == newPort && p.Name == changed.DisplayName)
                && !Volatile.Read(ref peers).Any(p => p.Port == originalPort && p.Name == changed.DisplayName));

            await using (var peer = new ChatNode("Проверка нового порта"))
            {
                string? name = null;
                peer.ConnectionChanged += info => { if (info is not null) name = info.Name; };
                peer.Start(0);
                await peer.ConnectAsync("127.0.0.1", newPort);
                if (name != changed.DisplayName)
                    throw new Exception("The new endpoint advertised the previous name.");
            }
            await viewModel.DisposeAsync();
            await using var restarted = new MainViewModel(dispatcher, store);
            await restarted.InitializeAsync();
            if (!restarted.IsListening || restarted.DisplayName != changed.DisplayName || restarted.LocalPort != newPort.ToString())
                throw new Exception("An app restart did not restore the applied profile.");
            await restarted.DisposeAsync();

            // Corrupted preferences must not prevent receiving connections.
            await File.WriteAllTextAsync(path, "{broken json");
            await using var fallback = new MainViewModel(dispatcher, store) { LocalPort = newPort.ToString() };
            await fallback.InitializeAsync();
            if (!fallback.IsListening || !fallback.HasError)
                throw new Exception("Corrupted preferences prevented startup or were not reported.");
            await fallback.DisposeAsync();

            // A failed save can be retried with the already active values.
            File.Delete(path);
            Directory.CreateDirectory(path);
            try
            {
                await using var unwritable = new MainViewModel(dispatcher, store) { LocalPort = newPort.ToString() };
                await unwritable.InitializeAsync();
                unwritable.DisplayName = "Сохранить повторно";
                await ApplyAsync(unwritable);
                if (!unwritable.IsListening || !unwritable.HasError || !unwritable.ApplyProfileCommand.CanExecute(null))
                    throw new Exception("A failed save did not leave the active profile available for retry.");
                Directory.Delete(path);
                await ApplyAsync(unwritable);
                if (unwritable.HasError || unwritable.HasProfileChanges
                    || (await store.LoadAsync())?.DisplayName != "Сохранить повторно")
                    throw new Exception("Retrying a failed save did not persist the applied profile.");
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path);
            }
        }
        finally { File.Delete(path); }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task ApplyAsync(MainViewModel viewModel)
    {
        if (!viewModel.ApplyProfileCommand.CanExecute(null))
            throw new Exception("Applying edited settings is unexpectedly disabled.");
        viewModel.ApplyProfileCommand.Execute(null);
        await UntilAsync(() => viewModel.CanEditProfile && !viewModel.IsRefreshing);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }
}
