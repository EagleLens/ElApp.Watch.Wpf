using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ElApp.Watch.Wpf.Converters;

/// <summary>Matches the original ToggleLabelsButton_Click's strikethrough-when-hidden behavior on "Aa".</summary>
public sealed class ShowLabelsToTextDecorationsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? null : TextDecorations.Strikethrough;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
