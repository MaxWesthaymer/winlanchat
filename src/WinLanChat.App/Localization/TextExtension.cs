using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using WinLanChat.Core.Localization;

namespace WinLanChat.App.Localization;

/// <summary>A binding keeps labels, templates and accessibility text current after a language change.</summary>
public sealed class TextExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{key}]") { Source = TranslationSource.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}

public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();
    private TranslationSource() => Strings.LanguageChanged += () =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    public string this[string key] => Strings.Get(key);
    public event PropertyChangedEventHandler? PropertyChanged;
}
