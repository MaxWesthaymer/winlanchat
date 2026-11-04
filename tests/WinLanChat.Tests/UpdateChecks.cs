using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Localization;
using WinLanChat.Core.Updates;

internal static class UpdateChecks
{
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(200_000);
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload));
    private const string DownloadUrl = "https://github.com/MaxWesthaymer/winlanchat/releases/download/v2.0.0/WinLanChat-2.0.0-win-x64-Setup.exe";
    private static readonly AppRelease Release = new(new Version(2, 0, 0), "Changes / Изменения", new Uri(DownloadUrl), Payload.Length, Digest);

    internal static async Task ServiceAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"WinLanChat-update-tests-{Guid.NewGuid():N}");
        try
        {
            var handler = new Handler();
            using var client = new HttpClient(handler);
            var service = new GitHubUpdateService(client, root);
            handler.Respond = (request, _) =>
            {
                Require(request.RequestUri!.AbsoluteUri.EndsWith("/releases/latest"), "Wrong release endpoint.");
                Require(request.Headers.UserAgent.Count > 0, "GitHub requests require a User-Agent.");
                return Task.FromResult(JsonResponse());
            };
            var release = await service.CheckAsync(new Version(1, 0, 3), default);
            Require(release == Release, "Release metadata was not preserved.");

            foreach (string tag in new[] { "v1.0.3", "v1.0.3.0", "1.0.2" })
            {
                using var json = JsonDocument.Parse(Metadata(tag));
                Require(GitHubUpdateService.ParseRelease(json.RootElement, new Version(1, 0, 3, 0)) is null,
                    "Equal/older versions must not offer an update.");
            }
            using (var json = JsonDocument.Parse(Metadata("v1.0.10")))
                Require(GitHubUpdateService.ParseRelease(json.RootElement, new Version(1, 0, 9))?.Version == new Version(1, 0, 10),
                    "Versions were compared as text.");
            foreach (string flag in new[] { "draft", "prerelease" })
            {
                handler.Respond = (_, _) => Task.FromResult(JsonResponse(Metadata().Replace($"\"{flag}\":false", $"\"{flag}\":true")));
                Require(await service.CheckAsync(new Version(1, 0, 0), default) is null, "An unstable release was offered.");
            }
            foreach (string invalid in new[]
            {
                "{broken", "[]", Metadata("v2.0.0-beta"),
                Metadata().Replace("win-x64-Setup.exe", "win-arm64-Setup.exe"),
                Metadata().Replace("sha256:" + Digest, ""),
                Metadata().Replace("https://github.com/", "http://github.com/"),
                Metadata().Replace("https://github.com/", "https://evil.example/"),
                Metadata().Replace("\"size\":200000", "\"size\":2147483648")
            })
            {
                handler.Respond = (_, _) => Task.FromResult(JsonResponse(invalid));
                await RejectAsync<InvalidDataException>(() => service.CheckAsync(new Version(1, 0, 0), default));
            }
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
            {
                handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status));
                await RejectAsync<InvalidOperationException>(() => service.CheckAsync(new Version(1, 0, 0), default));
            }
            handler.Respond = (_, _) => Task.FromResult(JsonResponse(new string('x', 1024 * 1024 + 1)));
            await RejectAsync<InvalidDataException>(() => service.CheckAsync(new Version(1, 0, 0), default));

            handler.Respond = (_, _) => Task.FromResult(BytesResponse(Payload));
            var progress = new InlineProgress();
            using (var downloaded = await service.DownloadAsync(Release, progress, default))
            {
                Require(File.ReadAllBytes(downloaded.Path).SequenceEqual(Payload), "Installer contents changed.");
                Require(progress.Values[0] == 0 && progress.Values[^1] == 100, "Missing download progress.");
                Require(progress.Values.Count <= 101, "Download progress flooded the UI dispatcher.");
                await RejectAsync<IOException>(() => File.WriteAllBytesAsync(downloaded.Path, [1, 2, 3]));
            }
            Require(!Directory.EnumerateFileSystemEntries(root).Any(), "Completed download was not cleaned up.");

            foreach (byte[] bytes in new[] { Payload[..^1], Payload.Concat(new byte[] { 0 }).ToArray(), new byte[Payload.Length] })
            {
                handler.Respond = (_, _) => Task.FromResult(BytesResponse(bytes));
                await RejectAsync<InvalidDataException>(() => service.DownloadAsync(Release, new InlineProgress(), default));
                Require(!Directory.EnumerateFileSystemEntries(root).Any(), "Invalid installer left partial files.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                handler.Respond = (_, _) => Task.FromResult(BytesResponse(Payload));
                await RejectAsync<OperationCanceledException>(() => service.DownloadAsync(Release,
                    new InlineProgress(value => { if (value > 0) cancellation.Cancel(); }), cancellation.Token));
                Require(!Directory.EnumerateFileSystemEntries(root).Any(), "Cancelled download left partial files.");
            }
            int requests = 0;
            handler.Respond = (_, _) =>
            {
                requests++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://evil.example/installer.exe");
                return Task.FromResult(response);
            };
            await RejectAsync<InvalidDataException>(() => service.DownloadAsync(Release, new InlineProgress(), default));
            Require(requests == 1, "The updater followed an untrusted redirect.");
            handler.Respond = (request, _) =>
            {
                if (request.RequestUri!.Host != "github.com") return Task.FromResult(BytesResponse(Payload));
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://release-assets.githubusercontent.com/installer.exe?signature=test");
                return Task.FromResult(response);
            };
            using var redirected = await service.DownloadAsync(Release, new InlineProgress(), default);
        }
        finally
        {
            // This unique directory is created by this test only.
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    internal static async Task UiAsync(Dispatcher dispatcher, Action<WinLanChat.App.MainWindow>? preview = null)
    {
        string originalLanguage = Strings.LanguageCode;
        var service = new FakeService();
        var interaction = new FakeInteraction();
        await using var main = new MainViewModel(dispatcher, updateService: service, updateInteraction: interaction);
        var updates = main.Updates;
        var window = new WinLanChat.App.MainWindow { DataContext = main };
        try
        {
            Require(!main.CheckForUpdatesOnStartup && service.Checks == 0, "Updates contacted GitHub without an explicit check.");
            var button = (Button)window.FindName("CheckUpdatesButton");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Require(ReferenceEquals(button.Command, updates.CheckCommand), "Update button did not bind to its view model.");
            service.PendingCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task checking = updates.CheckAsync();
            Require(updates.IsBusy && !updates.CheckCommand.CanExecute(null), "Overlapping checks were allowed.");
            updates.CancelCommand.Execute(null);
            await checking;
            Require(!updates.IsBusy && updates.Status == Strings.UpdateCancelled, "Cancellation did not restore update commands.");
            service.PendingCheck = null;
            await updates.CheckAsync();
            Require(updates.HasUpdate && updates.ReleaseNotes == Release.Notes, "The update offer was not displayed.");
            Strings.SetLanguage("en");
            Require(updates.Status == "Version 2.0.0 is available.", "English update status did not refresh.");
            Strings.SetLanguage("ru");
            Require(updates.Status == "Доступна версия 2.0.0.", "Russian update status did not refresh.");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.DataBind);
            Require(((TextBlock)window.FindName("UpdateStatusText")).Text == updates.Status, "The update status binding is stale.");
            updates.DownloadCommand.Execute(null);
            await UntilAsync(() => updates.IsReady && !updates.IsBusy);
            Require(!updates.CheckCommand.CanExecute(null) && interaction.Starts == 0, "Downloading started installation without consent.");
            if (preview is not null)
            {
                window.Show();
                main.IsSettingsOpen = true;
                await Task.Delay(300);
                ((TextBlock)window.FindName("UpdateStatusText")).BringIntoView();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                preview(window);
            }
            main.DraftAttachments.Add(new(Path.Combine(Path.GetTempPath(), "unsent.txt"), 1));
            updates.InstallCommand.Execute(null);
            Require(interaction.Confirmations == 0 && updates.Error == Strings.UpdateFilesPending, "Pending attachments did not block installation.");
            main.DraftAttachments.Clear();
            interaction.Consent = false;
            updates.InstallCommand.Execute(null);
            Require(interaction.Starts == 0 && updates.IsReady, "Declining installation lost the download or launched setup.");
            interaction.Consent = true;
            interaction.DuringConfirmation = () => main.DraftAttachments.Add(new("pending.txt", 1));
            updates.InstallCommand.Execute(null);
            Require(interaction.Starts == 0, "File state was not rechecked after the native confirmation dialog.");
            interaction.DuringConfirmation = null;
            main.DraftAttachments.Clear();
            interaction.FailStart = true;
            int exits = 0;
            updates.InstallationStarted += () => exits++;
            updates.InstallCommand.Execute(null);
            Require(updates.HasError && updates.IsReady && exits == 0, "A failed installer launch closed the app.");
            interaction.Consent = false;
            updates.InstallCommand.Execute(null);
            Require(!updates.HasError && updates.Status == Strings.UpdateReady, "Retrying left a stale installation error in the UI.");
            interaction.Consent = true;
            interaction.FailStart = false;
            updates.InstallCommand.Execute(null);
            Require(interaction.Starts == 1 && exits == 1, "Successful setup launch did not request application exit.");
            string path = service.DownloadPath!;
            await main.DisposeAsync();
            Require(!File.Exists(path), "Closing the app did not clean up its installer.");

            var pending = new FakeService { PendingCheck = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var closing = new UpdateViewModel(pending, interaction, () => false);
            Task operation = closing.CheckAsync();
            await closing.DisposeAsync();
            await operation;
            Require(!closing.IsBusy && !closing.CheckCommand.CanExecute(null), "Shutdown did not cancel the update request.");
        }
        finally
        {
            window.Close();
            Strings.SetLanguage(originalLanguage);
        }
    }

    private static string Metadata(string tag = "v2.0.0")
    {
        string version = tag.TrimStart('v');
        return JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease = false, body = Release.Notes,
            assets = new[] { new { name = $"WinLanChat-{version}-win-x64-Setup.exe", size = Payload.Length,
                browser_download_url = $"https://github.com/{GitHubUpdateService.Repository}/releases/download/{tag}/WinLanChat-{version}-win-x64-Setup.exe",
                digest = "sha256:" + Digest } }
        });
    }

    private static HttpResponseMessage JsonResponse(string? json = null) => new(HttpStatusCode.OK) { Content = new StringContent(json ?? Metadata()) };
    private static HttpResponseMessage BytesResponse(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(new ChunkedStream(bytes))
    };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Respond(request, cancellationToken);
    }
    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
    }
    private sealed class InlineProgress(Action<double>? callback = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) { Values.Add(value); callback?.Invoke(value); }
    }
    private sealed class FakeService : IAppUpdateService
    {
        public int Checks { get; private set; }
        public TaskCompletionSource<AppRelease?>? PendingCheck { get; set; }
        public string? DownloadPath { get; private set; }
        public Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            Checks++;
            return PendingCheck?.Task.WaitAsync(cancellationToken) ?? Task.FromResult<AppRelease?>(Release);
        }
        public async Task<DownloadedUpdate> DownloadAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken)
        {
            string directory = Path.Combine(Path.GetTempPath(), $"WinLanChat-update-ui-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            DownloadPath = Path.Combine(directory, "installer.exe");
            await File.WriteAllBytesAsync(DownloadPath, Payload, cancellationToken);
            progress.Report(100);
            return new(DownloadPath, File.Open(DownloadPath, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
    }
    private sealed class FakeInteraction : IUpdateInteraction
    {
        public bool Consent { get; set; }
        public int Confirmations { get; private set; }
        public int Starts { get; private set; }
        public bool FailStart { get; set; }
        public Action? DuringConfirmation { get; set; }
        public bool ConfirmInstallation(string version) { Confirmations++; DuringConfirmation?.Invoke(); return Consent; }
        public void StartInstaller(string path)
        {
            if (FailStart) throw new IOException("Test launch failure");
            Require(File.Exists(path), "Installer was deleted before launch.");
            Starts++;
        }
    }
}
