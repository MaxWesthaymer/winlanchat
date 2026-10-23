using System.Globalization;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinLanChat.App.Models;
using WinLanChat.App.Services;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Localization;
using WinLanChat.Core.Models;

internal static class LocalizationChecks
{
    internal static Task ResourcesAsync()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        string originalLanguage = Strings.LanguageCode;
        try
        {
            var russian = new ResourceManager("WinLanChat.Core.Localization.russian", typeof(Strings).Assembly)
                .GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
            var english = new ResourceManager("WinLanChat.Core.Localization.english", typeof(Strings).Assembly)
                .GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
            var russianKeys = russian.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet();
            if (!russianKeys.SetEquals(english.Cast<DictionaryEntry>().Select(entry => (string)entry.Key)))
                throw new Exception("Russian and English resource keys differ.");
            foreach (string key in russianKeys)
                if (CompositeFormat.Parse(russian.GetString(key)!).MinimumArgumentCount
                    != CompositeFormat.Parse(english.GetString(key)!).MinimumArgumentCount)
                    throw new Exception($"Translation placeholders differ for {key}.");

            // The explicit selection, including number formatting, overrides the Windows culture.
            foreach (string language in new[] { "ru-RU", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (string selectedLanguage in new[] { "ru", "en" })
                {
                    Strings.SetLanguage(selectedLanguage);
                    foreach (PropertyInfo property in typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static))
                    {
                        string value = (string)property.GetValue(null)!;
                        if (string.IsNullOrWhiteSpace(value))
                            throw new Exception($"Localization resource {property.Name} is empty.");
                        _ = CompositeFormat.Parse(value);
                    }

                    bool isEnglish = selectedLanguage == "en";
                    if (Strings.Settings != (isEnglish ? "Settings" : "Настройки")
                        || Strings.NetworkComputers != (isEnglish ? "NETWORK COMPUTERS" : "КОМПЬЮТЕРЫ В СЕТИ")
                        || Strings.Format(Strings.InvalidMessageLength, 8000) != (isEnglish
                            ? "The message must contain 1 to 8000 characters." : "Сообщение должно содержать от 1 до 8000 символов."))
                        throw new Exception("Selected language or parameter formatting failed.");

                    string expectedSize = isEnglish ? "1.5 KB" : "1,5 КБ";
                    if (FileAttachmentViewModel.FormatSize(1536) != expectedSize)
                        throw new Exception("Localized file sizes do not respect the selected language.");

                    var attachment = new FileAttachmentViewModel(new FileOffer(Guid.NewGuid(), "", "sample.bin", 2048),
                        true, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
                    attachment.Update(new(attachment.Id, FileTransferState.Transferring, 1024));
                    if (attachment.Status != (isEnglish ? "Sending · 50% · 1 KB / 2 KB" : "Отправка · 50% · 1 КБ / 2 КБ"))
                        throw new Exception("The localized file transfer template lost progress or size values.");
                }
            }
            try { Strings.SetLanguage("de"); throw new Exception("Unsupported language accepted."); }
            catch (ArgumentException) { }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            Strings.SetLanguage(originalLanguage);
        }
        return Task.CompletedTask;
    }

    internal static void CheckWindow(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1100, 690));
        content.Arrange(new Rect(0, 0, 1100, 690));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(1100, 690, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (window.Title != "WinLanChat"
            || !Descendants<TextBlock>(content).Any(text => text.Text == Strings.NetworkComputers)
            || !Descendants<Button>(content).Any(button => Equals(button.Content, Strings.Send))
            || !Descendants<ListBox>(content).Any(list => AutomationProperties.GetName(list) == Strings.NetworkComputersAccessibleName))
            throw new Exception("The WPF window did not load localized labels and accessibility text.");
    }

    internal static async Task LanguageSettingsAsync(Window window, Action? renderEnglish = null)
    {
        object originalContext = window.DataContext;
        string originalLanguage = Strings.LanguageCode;
        string path = Path.Combine(Path.GetTempPath(), $"WinLanChat-language-{Guid.NewGuid():N}.json");
        var store = new ProfileSettingsStore(path);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            await store.SaveAsync(new ProfileSettings("Language test", port));
            await using var viewModel = new MainViewModel(window.Dispatcher, store);
            await viewModel.InitializeAsync();
            window.DataContext = viewModel;
            viewModel.IsSettingsOpen = true;
            await Dispatcher.Yield(DispatcherPriority.Background);
            var combo = (ComboBox)window.FindName("LanguageComboBox");
            combo.SelectedValue = "en";
            if (viewModel.SelectedLanguageCode != "en" || Strings.LanguageCode != "ru")
                throw new Exception("The language selector did not stage the selection.");
            viewModel.ResetProfileCommand.Execute(null);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (!Equals(combo.SelectedValue, "ru") || viewModel.HasProfileChanges)
                throw new Exception("Discarding a language selection did not restore the saved language.");

            await using var peer = new WinLanChat.Core.Networking.ChatNode("Language peer");
            peer.Start(0);
            await peer.ConnectAsync("127.0.0.1", port);
            await UntilAsync(() => viewModel.IsConnected);
            await peer.SendAsync("Keep this conversation");
            await UntilAsync(() => viewModel.Messages.Count > 0);
            var attachment = new FileAttachmentViewModel(new FileOffer(Guid.NewGuid(), "", "sample.bin", 1536),
                true, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
            viewModel.Messages.Add(new ChatMessage("Вы", "Outgoing", true, DateTimeOffset.Now) { Attachment = attachment });
            viewModel.DraftAttachments.Add(new DraftAttachmentViewModel("draft.bin", 1536));
            viewModel.Draft = "Keep this draft";
            string? attachmentChange = null;
            attachment.PropertyChanged += (_, args) => attachmentChange = args.PropertyName;
            combo.SelectedValue = "en";
            viewModel.DisplayName += " draft";
            if (viewModel.ApplyProfileCommand.CanExecute(null))
                throw new Exception("Language settings allowed a network profile change during a conversation.");
            viewModel.DisplayName = "Language test";
            if (!viewModel.ApplyProfileCommand.CanExecute(null))
                throw new Exception("Changing only the language during a conversation was disabled.");
            viewModel.ApplyProfileCommand.Execute(null);
            await UntilAsync(() => viewModel.CanEditProfile && !viewModel.HasProfileChanges);
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (viewModel.HasError || !viewModel.IsConnected || viewModel.Messages.Count != 2
                || viewModel.Draft != "Keep this draft" || viewModel.Messages[1].DisplayAuthor != "You"
                || attachment.FileSize != "1.5 KB" || attachment.Status != "Waiting for the recipient to accept…"
                || attachmentChange != nameof(FileAttachmentViewModel.Status)
                || viewModel.Status != "Connection established" || !viewModel.LocalAddresses.Contains("port")
                || (await store.LoadAsync())?.LanguageCode != "en")
                throw new Exception("Applying English lost the conversation, draft, state or saved choice.");
            CheckWindow(window);
            if (renderEnglish is not null)
                await Task.Delay(400); // Let the Fluent Expander's size animation settle before capturing it.
            renderEnglish?.Invoke();
            if (AutomationProperties.GetName(combo) != "Language")
                throw new Exception("The language selector's accessibility text was not updated.");
            await peer.SendAsync("Still connected in English");
            await UntilAsync(() => viewModel.Messages.Any(message => message.Text == "Still connected in English"));
            await viewModel.DisposeAsync();
            Strings.SetLanguage("ru");
            await using var restarted = new MainViewModel(window.Dispatcher, store);
            await restarted.InitializeAsync();
            if (restarted.SelectedLanguageCode != "en" || Strings.Settings != "Settings")
                throw new Exception("Restarting did not restore the saved language.");
            window.DataContext = restarted;
            restarted.SelectedLanguageCode = "ru";
            restarted.ApplyProfileCommand.Execute(null);
            await UntilAsync(() => restarted.CanEditProfile && !restarted.HasProfileChanges);
            await Dispatcher.Yield(DispatcherPriority.Background);
            CheckWindow(window);
        }
        finally
        {
            window.DataContext = originalContext;
            Strings.SetLanguage(originalLanguage);
            File.Delete(path);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    // Load and render the real XAML without acquiring the running messenger's instance lock.
    internal static Task WindowAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
                var application = new WinLanChat.App.App();
                application.InitializeComponent();
                var window = new WinLanChat.App.MainWindow();
                try
                {
                    foreach (string code in new[] { "ru", "en", "ru" })
                    {
                        Strings.SetLanguage(code);
                        CheckWindow(window);
                    }
                }
                finally { window.Close(); application.Shutdown(); }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
