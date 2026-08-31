using System.Globalization;
using System.Windows.Data;

namespace ElApp.Watch.Wpf.Converters;

/// <summary>Matches the original ToggleLabelsButton_Click's `ToggleLabelsButton.Opacity = _showStatusLabels ? 1.0 : 0.55`.</summary>
public sealed class ShowLabelsToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.55;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
