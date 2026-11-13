using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using BleHid.Localization;

namespace BleHid.App.Localization;

/// <summary>
/// <c>{l:Tr app.nav.status}</c> in XAML. The language is fixed for the life of the process, so
/// the value is resolved once instead of being bound.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}

/// <summary>
/// A binding converter that inserts the bound value into a translated string with one
/// placeholder: <c>{Binding Target, Converter={l:Format app.capture.sendingTo}}</c>.
/// </summary>
public sealed class FormatExtension : MarkupExtension, IValueConverter
{
    public FormatExtension() { }

    public FormatExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => this;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Loc.Fill(Key, value);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
