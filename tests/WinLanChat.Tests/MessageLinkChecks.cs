using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using WinLanChat.App.Behaviors;
using WinLanChat.App.Services;

internal static class MessageLinkChecks
{
    internal static Task ParsingAsync()
    {
        const string text = "Сайт https://example.com/path?q=1&lang=ru, (http://192.168.1.10:8080/help).\n"
            + "www.example.org и https://en.wikipedia.org/wiki/Function_(mathematics). "
            + "https://пример.рф/путь#раздел";
        string[] expected = ["https://example.com/path?q=1&lang=ru", "http://192.168.1.10:8080/help",
            "www.example.org", "https://en.wikipedia.org/wiki/Function_(mathematics)", "https://пример.рф/путь#раздел"];
        var links = MessageLinkParser.FindLinks(text).ToArray();
        if (!links.Select(link => text.Substring(link.Start, link.Length)).SequenceEqual(expected)
            || links[2].Address.AbsoluteUri != "https://www.example.org/")
            throw new Exception("Web addresses, punctuation or www normalization were parsed incorrectly.");

        const string unsafeText = "javascript:alert(1) file:///C:/Windows/cmd.exe ftp://example.com mailto:a@example.com http:// www.";
        if (MessageLinkParser.FindLinks(unsafeText).Any())
            throw new Exception("A non-web or invalid address became a link.");
        foreach (string address in new[] { "file:///C:/Windows/cmd.exe", "javascript:alert(1)", "ftp://example.com", "/relative" })
        {
            var uri = new Uri(address, UriKind.RelativeOrAbsolute);
            if (BrowserLauncher.IsWebAddress(uri))
                throw new Exception("A non-web address can be launched.");
            try
            {
                BrowserLauncher.Open(uri);
                throw new Exception("A non-web address was launched.");
            }
            catch (ArgumentException) { }
        }

        string large = "https://example.com/" + new string(')', 7980);
        var trimmed = MessageLinkParser.FindLinks(large).Single();
        if (large.Substring(trimmed.Start, trimmed.Length) != "https://example.com/")
            throw new Exception("Long trailing punctuation was not removed.");
        return Task.CompletedTask;
    }

    internal static void CheckRendering(Window window)
    {
        const string text = "Текст <b>без разметки</b> https://example.com/path?q=1&b=2.\nВторая строка www.example.org";
        var view = new RichTextBox { Style = (Style)window.FindResource("MessageText") };
        MessageLinks.SetText(view, text);
        view.ApplyTemplate();
        view.Measure(new Size(300, 500));
        view.Arrange(new Rect(0, 0, 300, view.DesiredSize.Height));
        var paragraph = (Paragraph)view.Document.Blocks.Single();
        if (!view.IsReadOnly || !view.IsDocumentEnabled || paragraph.Inlines.OfType<Hyperlink>().Count() != 2)
            throw new Exception("Message links are not enabled in the read-only message view.");
        string displayed = new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text;
        if (displayed != text + "\r\n")
            throw new Exception("Rendering links changed the message text or line breaks.");
        view.SelectAll();
        if (view.Selection.Text != displayed || !ApplicationCommands.Copy.CanExecute(null, view))
            throw new Exception("Message text can no longer be selected and copied.");

        // Recycled message containers must discard links belonging to the previous message.
        MessageLinks.SetText(view, "Обычный текст");
        if (((Paragraph)view.Document.Blocks.Single()).Inlines.OfType<Hyperlink>().Any())
            throw new Exception("Reusing a message view retained stale links.");
    }
}
