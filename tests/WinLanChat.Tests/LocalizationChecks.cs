using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinLanChat.App.ViewModels;
using WinLanChat.Core.Localization;
using WinLanChat.Core.Models;

internal static class LocalizationChecks
{
    internal static Task ResourcesAsync()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            // Russian must remain available even on a non-Russian Windows installation.
            foreach (string language in new[] { "ru-RU", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
                foreach (PropertyInfo property in typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    string value = (string)property.GetValue(null)!;
                    if (string.IsNullOrWhiteSpace(value))
                        throw new Exception($"Localization resource {property.Name} is empty.");
                    _ = CompositeFormat.Parse(value);
                }

                if (Strings.Settings != "Настройки" || Strings.NetworkComputers != "КОМПЬЮТЕРЫ В СЕТИ"
                    || Strings.Format(Strings.InvalidMessageLength, 8000) != "Сообщение должно содержать от 1 до 8000 символов.")
                    throw new Exception("Russian resource fallback or parameter formatting failed.");

                string expectedSize = language == "ru-RU" ? "1,5 КБ" : "1.5 КБ";
                if (FileAttachmentViewModel.FormatSize(1536) != expectedSize)
                    throw new Exception("Localized file sizes do not respect the current number format.");

                var attachment = new FileAttachmentViewModel(new FileOffer(Guid.NewGuid(), "", "sample.bin", 2048),
                    true, () => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask, _ => { });
                attachment.Update(new(attachment.Id, FileTransferState.Transferring, 1024));
                if (attachment.Status != "Отправка · 50% · 1 КБ / 2 КБ")
                    throw new Exception("The localized file transfer template lost progress or size values.");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
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
            || !Descendants<TextBlock>(content).Any(text => text.Text == "КОМПЬЮТЕРЫ В СЕТИ")
            || !Descendants<Button>(content).Any(button => Equals(button.Content, "Отправить  →"))
            || !Descendants<ListBox>(content).Any(list => AutomationProperties.GetName(list) == "Компьютеры в сети"))
            throw new Exception("The WPF window did not load Russian labels and accessibility text.");
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
                try { CheckWindow(window); }
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
