using System.Windows.Media;

namespace ElApp.Watch.Wpf.ViewModels;

/// <summary>
/// Shared brushes matching MainWindow.xaml's resource dictionary colors, for use from
/// view-model code that can't call FrameworkElement.FindResource (it isn't attached to a
/// visual tree). Kept in exact color parity with the XAML resources of the same names.
/// </summary>
public static class AppBrushes
{
    public static readonly Brush Tile = Freeze(0x1B, 0x1F, 0x29);
    public static readonly Brush Border = Freeze(0x26, 0x2B, 0x36);
    public static readonly Brush Accent = Freeze(0x22, 0xD3, 0xEE);
    public static readonly Brush TextPrimary = Freeze(0xE5, 0xE7, 0xEB);
    public static readonly Brush TextSecondary = Freeze(0x8B, 0x93, 0xA7);
    public static readonly Brush Online = Freeze(0x34, 0xD3, 0x99);
    public static readonly Brush Offline = Freeze(0xF8, 0x71, 0x71);
    public static readonly Brush Amber = Freeze(0xFB, 0xBF, 0x24);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
