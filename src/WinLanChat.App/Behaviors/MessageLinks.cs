using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using WinLanChat.App.Services;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Behaviors;

public static class MessageLinks
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached("Text", typeof(string), typeof(MessageLinks),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, OnTextChanged));

    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not RichTextBox textBox)
            return;

        string text = args.NewValue as string ?? "";
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 22 };
        int position = 0;
        foreach (var link in MessageLinkParser.FindLinks(text))
        {
            if (link.Start > position)
                paragraph.Inlines.Add(new Run(text[position..link.Start]));

            var hyperlink = new Hyperlink(new Run(text.Substring(link.Start, link.Length)))
            {
                NavigateUri = link.Address,
                ToolTip = link.Address.AbsoluteUri
            };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
            hyperlink.RequestNavigate += OnRequestNavigate;
            paragraph.Inlines.Add(hyperlink);
            position = link.Start + link.Length;
        }
        if (position < text.Length)
            paragraph.Inlines.Add(new Run(text[position..]));

        textBox.Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        try { BrowserLauncher.Open(args.Uri); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(Strings.BrowserOpenFailed,
                Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
