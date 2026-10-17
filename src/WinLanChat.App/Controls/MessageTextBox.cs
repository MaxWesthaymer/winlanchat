using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinLanChat.App.Behaviors;

namespace WinLanChat.App.Controls;

public sealed class MessageTextBox : RichTextBox
{
    protected override Size MeasureOverride(Size constraint)
    {
        var text = new FormattedText(
            MessageLinks.GetText(this), CultureInfo.CurrentCulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
            Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // RichTextBox reserves five DIPs on each side of its document for the caret.
        double width = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 10
            + Padding.Left + Padding.Right + BorderThickness.Left + BorderThickness.Right;

        // Measure the document at its natural width, allowing wrapping in narrower bubbles.
        return base.MeasureOverride(new Size(Math.Min(width, constraint.Width), constraint.Height));
    }
}
