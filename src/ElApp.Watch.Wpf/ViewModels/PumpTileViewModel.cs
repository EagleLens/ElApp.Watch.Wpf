using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ElApp.Watch.Domain;
using ElApp.Watch.Vision;

namespace ElApp.Watch.Wpf.ViewModels;

/// <summary>
/// Presentation state for one camera tile - both the four "dynamic" tiles (a real camera/video
/// source with vehicle-stop detection) and the static filler tiles that pad the grid out to the
/// randomized pump count. Reproduces MainWindow.xaml.cs's original TileHandle fields as bindable
/// properties for the ItemsControl/DataTemplate in PumpTileView.xaml.
/// </summary>
public partial class PumpTileViewModel : ObservableObject
{
    private static readonly Dictionary<PumpState, (string Text, Brush Brush, string Icon)> VehicleStatusDisplay = new()
    {
        [PumpState.Empty] = ("Pump empty", AppBrushes.TextSecondary, "○"),
        [PumpState.VehicleComing] = ("Vehicle coming", AppBrushes.Accent, "→"),
        [PumpState.VehicleStopping] = ("Vehicle stopping", AppBrushes.Amber, "⧖"),
        [PumpState.TakingPhoto] = ("Taking photo", AppBrushes.Offline, "◉"),
        [PumpState.VehicleStopped] = ("Vehicle stopped", AppBrushes.Online, "✓"),
    };

    private bool _showStatusLabels = true;
    private DispatcherTimer? _transientStatusRevertTimer;

    public PumpTileViewModel(int pumpNumber, bool isDynamic)
    {
        PumpNumber = pumpNumber;
        IsDynamic = isDynamic;

        string assetPath = $"pack://application:,,,/Assets/pump{((pumpNumber - 1) % 4) + 1}.jpg";
        VideoSource = new BitmapImage(new Uri(assetPath, UriKind.Absolute));

        if (isDynamic)
        {
            TileDotBrush = AppBrushes.TextSecondary;
            SidebarDotBrush = AppBrushes.TextSecondary;
            BadgeText = "DETECTING";
            BadgeBrush = AppBrushes.TextSecondary;
            (VehicleStatusText, VehicleStatusBrush, VehicleStatusIcon) = VehicleStatusDisplay[PumpState.Empty];
        }
        else
        {
            TileDotBrush = AppBrushes.Online;
            SidebarDotBrush = AppBrushes.Online;
            BadgeText = "LIVE";
            BadgeBrush = AppBrushes.Offline; // matches the original's LiveBadgeRedBrush (same F87171 color)
        }
    }

    public int PumpNumber { get; }
    public bool IsDynamic { get; }
    public string PumpId => $"Pump{PumpNumber}";
    public string DisplayName => $"Pump {PumpNumber}";

    /// <summary>The tile's video/thumbnail image. Starts as the static placeholder photo; live tiles
    /// replace it with captured frames once their camera/video source starts publishing.</summary>
    [ObservableProperty]
    private ImageSource _videoSource;

    [ObservableProperty]
    private Brush _tileDotBrush;

    [ObservableProperty]
    private Brush _sidebarDotBrush;

    [ObservableProperty]
    private string _badgeText = string.Empty;

    [ObservableProperty]
    private Brush _badgeBrush = AppBrushes.TextSecondary;

    [ObservableProperty]
    private bool _online;

    [ObservableProperty]
    private ImageSource? _snapshotImage;

    [ObservableProperty]
    private bool _snapshotOverlayVisible;

    [ObservableProperty]
    private string _vehicleStatusIcon = string.Empty;

    [ObservableProperty]
    private string _vehicleStatusText = string.Empty;

    [ObservableProperty]
    private Brush _vehicleStatusBrush = AppBrushes.TextSecondary;

    [ObservableProperty]
    private bool _vehicleStatusTextVisible = true;

    /// <summary>The attached pump monitor for a dynamic tile, once its camera/video source starts. Null for filler tiles.</summary>
    internal PumpMonitor? Monitor { get; set; }

    /// <summary>Publishes a newly-captured video frame - kept as a method (like every other tile mutation
    /// below) so CameraSourceService doesn't need to know which raw property backs the display.</summary>
    public void SetVideoFrame(BitmapSource frame) => VideoSource = frame;

    /// <summary>Shows a just-captured snapshot overlay image.</summary>
    public void ShowSnapshot(BitmapSource snapshot)
    {
        SnapshotImage = snapshot;
        SnapshotOverlayVisible = true;
    }

    /// <summary>Sets the tile's online/offline badge and status-dot color, mirroring the original SetTileStatus.</summary>
    public void SetOnlineStatus(bool online, string badgeText)
    {
        Brush brush = online ? AppBrushes.Online : AppBrushes.Offline;
        TileDotBrush = brush;
        SidebarDotBrush = brush;
        BadgeText = badgeText;
        BadgeBrush = brush;
        Online = online;
    }

    /// <summary>Applies a pump state's icon/text/color, mirroring the original ApplyVehicleStatus.</summary>
    public void ApplyVehicleStatus(PumpState state)
    {
        (string text, Brush brush, string icon) = VehicleStatusDisplay[state];
        SetStatusVisual(text, brush, icon);

        if (state == PumpState.Empty)
        {
            SnapshotOverlayVisible = false;
        }
    }

    /// <summary>Applies the global show/hide-labels toggle to this tile, mirroring the original per-tile loop in ToggleLabelsButton_Click.</summary>
    public void SetShowStatusLabels(bool show)
    {
        _showStatusLabels = show;
        VehicleStatusTextVisible = show;
    }

    private void SetStatusVisual(string text, Brush brush, string icon)
    {
        VehicleStatusIcon = icon;
        VehicleStatusBrush = brush;
        VehicleStatusText = text;
        VehicleStatusTextVisible = _showStatusLabels;
    }

    /// <summary>
    /// Overrides the status badge for a few seconds (e.g. to confirm a capture just happened),
    /// then restores whatever the pump's actual current state should show - mirrors the original
    /// ShowTransientStatus, including its 3.5s revert timer.
    /// </summary>
    public void ShowTransientStatus(string text, Brush brush, string icon)
    {
        if (!IsDynamic)
        {
            return;
        }

        SetStatusVisual(text, brush, icon);

        _transientStatusRevertTimer?.Stop();
        _transientStatusRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _transientStatusRevertTimer.Tick += (_, _) =>
        {
            _transientStatusRevertTimer!.Stop();
            if (Monitor is not null)
            {
                ApplyVehicleStatus(Monitor.State);
            }
        };
        _transientStatusRevertTimer.Start();
    }
}
