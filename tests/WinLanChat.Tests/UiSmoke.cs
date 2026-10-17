using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinLanChat.App.Models;
using WinLanChat.App.ViewModels;

internal static class UiSmoke
{
    internal static Task RunAsync(string? previewDirectory)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new WinLanChat.App.App();
                application.InitializeComponent();
                var viewModel = new MainViewModel(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                var window = new WinLanChat.App.MainWindow { DataContext = viewModel };
                var content = (FrameworkElement)window.Content;
                Render(content, 920, 570, previewDirectory, "window-empty.png");
                viewModel.Messages.Add(new ChatMessage("Компьютер Б", "Привет! Проверяем связь в локальной сети. 👋", false, DateTimeOffset.Now));
                viewModel.Messages.Add(new ChatMessage("Вы", "Привет! Всё работает.\nЭто сообщение в несколько строк.", true, DateTimeOffset.Now));
                viewModel.Messages.Add(new ChatMessage("Компьютер Б", new string('Ж', 200), false, DateTimeOffset.Now));
                Render(content, 1100, 690, previewDirectory, "window-conversation.png");
                window.Close();
                application.Shutdown();
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void Render(FrameworkElement content, int width, int height, string? directory, string filename)
    {
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        if (directory is null)
            return;
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, filename));
        encoder.Save(output);
    }
}
