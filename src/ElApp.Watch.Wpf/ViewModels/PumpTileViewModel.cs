using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ElApp.Watch.Domain;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services;

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

    /// <summary>
    /// Border color for the snapshot overlay: neutral (Accent) while a just-taken photo's
    /// process-image result hasn't come back yet, then green/red/amber for Valid/Invalid/Warning (see
    /// <see cref="SetSnapshotResult"/>). Reset to neutral by <see cref="ShowSnapshot"/> on every new
    /// capture, so a stale result from a previous vehicle never lingers on the next one's still-pending
    /// result.
    /// </summary>
    [ObservableProperty]
    private Brush _snapshotResultBrush = AppBrushes.Accent;

    [ObservableProperty]
    private string _vehicleStatusIcon = string.Empty;

    [ObservableProperty]
    private string _vehicleStatusText = string.Empty;

    [ObservableProperty]
    private Brush _vehicleStatusBrush = AppBrushes.TextSecondary;

    [ObservableProperty]
    private bool _vehicleStatusTextVisible = true;

    /// <summary>
    /// This tile's status text at the moment it last took a photo (e.g. "Pump 3 - Taking photo"), for
    /// MainViewModel's single-line status bar - the only status bar-worthy event; every other state
    /// change is left unreported. Untouched (not cleared) between photos, so the bar keeps showing the
    /// most recent capture until another pump takes the next one.
    /// </summary>
    [ObservableProperty]
    private string? _lastSnapshotStatus;

    /// <summary>Whether the reg/result panel has anything to show at all. Set true the moment a photo
    /// is captured and its process-image request is sent (see <see cref="BeginResultCapture"/>), and
    /// only cleared once the vehicle actually leaves (<see cref="ApplyVehicleStatus"/> on
    /// <see cref="PumpState.Empty"/>) - it stays up across the whole visit, not just one capture.</summary>
    [ObservableProperty]
    private bool _resultPanelVisible;

    /// <summary>True while a process-image request for the current capture is in flight - drives the
    /// panel's loading indicator. Cleared by <see cref="SetSnapshotResult"/> once the response (or
    /// failure) comes back.</summary>
    [ObservableProperty]
    private bool _resultLoading;

    /// <summary>True for a Valid result: a small, unobtrusive tick badge on the tile's side - Valid is
    /// the expected/good outcome, so it doesn't need to interrupt the video feed the way Invalid/Warning
    /// do.</summary>
    [ObservableProperty]
    private bool _resultSideVisible;

    /// <summary>True for Invalid: a corner badge on the tile's right side, mirroring Valid's on the
    /// left - bigger and animated (see PumpTileView.xaml's pulse trigger) since this is a fraud/fake-plate
    /// signal that genuinely needs an operator's attention, but it still shouldn't block the video feed
    /// the way a full center overlay would.</summary>
    [ObservableProperty]
    private bool _resultInvalidVisible;

    /// <summary>True for Warning: the center-screen glyph + label + remarks - an uncertain read is worth
    /// a fuller, more deliberate look than a corner badge gives.</summary>
    [ObservableProperty]
    private bool _resultWarningVisible;

    /// <summary>The plate text shown in the UK-plate-styled readout: the local OCR read the instant the
    /// photo's taken (<see cref="BeginResultCapture"/>), overwritten with the API's own reading once its
    /// response comes back (<see cref="SetSnapshotResult"/>) - they don't always agree.</summary>
    [ObservableProperty]
    private string? _resultRegText;

    /// <summary>Big glyph for the result panel: check for Valid, cross for Invalid, warning triangle
    /// for Warning.</summary>
    [ObservableProperty]
    private string _resultIcon = string.Empty;

    [ObservableProperty]
    private Brush _resultIconBrush = AppBrushes.Accent;

    /// <summary>"WARNING" label shown under the glyph - only set for a Warning result; null (and so
    /// blank) for Valid/Invalid, where the glyph alone is enough.</summary>
    [ObservableProperty]
    private string? _resultStatusLabel;

    [ObservableProperty]
    private bool _resultStatusLabelVisible;

    [ObservableProperty]
    private string? _resultRemarksText;

    [ObservableProperty]
    private bool _resultRemarksVisible;

    /// <summary>The attached pump monitor for a dynamic tile, once its camera/video source starts. Null for filler tiles.</summary>
    internal PumpMonitor? Monitor { get; set; }

    /// <summary>Publishes a newly-captured video frame - kept as a method (like every other tile mutation
    /// below) so CameraSourceService doesn't need to know which raw property backs the display.</summary>
    public void SetVideoFrame(BitmapSource frame) => VideoSource = frame;

    /// <summary>Shows a just-captured snapshot overlay image. The reg/result panel is a separate
    /// lifecycle (see <see cref="BeginResultCapture"/>/<see cref="SetSnapshotResult"/>) - a photo can be
    /// retaken more than once per visit, but the panel spans the whole visit, so it's not touched here.</summary>
    public void ShowSnapshot(BitmapSource snapshot)
    {
        SnapshotImage = snapshot;
        SnapshotOverlayVisible = true;
        SnapshotResultBrush = AppBrushes.Accent;
    }

    /// <summary>
    /// Puts the reg/result panel into its loading state the moment a photo's taken and its process-image
    /// request is about to go out - a UK-plate-styled box showing the local OCR's best guess (or a
    /// placeholder if it couldn't read one) plus a loading indicator, so the pump shows *something*
    /// immediately instead of leaving the corner blank for however long the API call takes.
    /// </summary>
    public void BeginResultCapture(string? localPlateText)
    {
        ResultPanelVisible = true;
        ResultLoading = true;
        ResultSideVisible = false;
        ResultInvalidVisible = false;
        ResultWarningVisible = false;
        ResultRegText = string.IsNullOrWhiteSpace(localPlateText) ? "SCANNING" : localPlateText.ToUpperInvariant();
        ResultIcon = string.Empty;
        ResultStatusLabel = null;
        ResultStatusLabelVisible = false;
        ResultRemarksText = null;
        ResultRemarksVisible = false;
    }

    /// <summary>
    /// Applies the process-image API's result to the tile: the snapshot overlay's border (green for
    /// Valid, red for Invalid, amber for Warning - matching the amber already used for VehicleStopping's
    /// status color) plus the reg/result panel. Valid and Invalid are both corner badges (left/right) so
    /// neither blocks the video feed; Warning gets the fuller center-screen treatment since an uncertain
    /// read is worth a more deliberate look. The API's Remarks (the actual reason - a registry mismatch,
    /// an unreadable plate, low confidence, ...) are shown verbatim for Invalid/Warning rather than a
    /// generic "INVALID"/"WARNING" label, since the remark is what's actually useful to an operator.
    /// Called once the result actually comes back, ending the loading state <see cref="BeginResultCapture"/>
    /// started. A null/unmapped result (call failed, or returned nothing usable) has nothing worth keeping
    /// on screen, so the whole panel is hidden rather than leaving the loading spinner stuck forever.
    /// </summary>
    public void SetSnapshotResult(VehicleImageResultData? data)
    {
        (Brush? brush, string icon) = data?.Result switch
        {
            VehicleProcessingResult.Valid => ((Brush?)AppBrushes.Online, "✓"),
            VehicleProcessingResult.Invalid => (AppBrushes.Offline, "✗"),
            VehicleProcessingResult.Warning => (AppBrushes.Amber, "⚠"),
            _ => (null, string.Empty),
        };

        ResultLoading = false;

        if (brush is null)
        {
            ResultPanelVisible = false;
            ResultSideVisible = false;
            ResultInvalidVisible = false;
            ResultWarningVisible = false;
            return;
        }

        SnapshotResultBrush = brush;
        ResultIconBrush = brush;
        ResultIcon = icon;
        if (!string.IsNullOrWhiteSpace(data!.Reg))
        {
            ResultRegText = data.Reg.ToUpperInvariant();
        }

        bool isValid = data.Result == VehicleProcessingResult.Valid;
        bool isInvalid = data.Result == VehicleProcessingResult.Invalid;
        bool isWarning = data.Result == VehicleProcessingResult.Warning;

        ResultStatusLabel = isWarning ? "WARNING" : null;
        ResultStatusLabelVisible = isWarning;
        ResultRemarksText = isInvalid || isWarning ? data.Remarks : null;
        ResultRemarksVisible = !string.IsNullOrWhiteSpace(ResultRemarksText);

        ResultSideVisible = isValid;
        ResultInvalidVisible = isInvalid;
        ResultWarningVisible = isWarning;
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

        string? snapshotStatus = ResolveSnapshotStatus(PumpNumber, state);
        if (snapshotStatus is not null)
        {
            LastSnapshotStatus = snapshotStatus;
        }

        if (state == PumpState.Empty)
        {
            SnapshotOverlayVisible = false;

            // The vehicle's actually gone - this is the only point the reg/result panel is cleared;
            // it otherwise spans every capture across the whole visit (BeginResultCapture/SetSnapshotResult).
            ResultPanelVisible = false;
            ResultLoading = false;
            ResultSideVisible = false;
            ResultInvalidVisible = false;
            ResultWarningVisible = false;
            ResultRegText = null;
            ResultIcon = string.Empty;
            ResultStatusLabel = null;
            ResultStatusLabelVisible = false;
            ResultRemarksText = null;
            ResultRemarksVisible = false;
        }
    }

    /// <summary>
    /// The only state that's status bar-worthy: a photo just being taken. Pulled out as a pure, static,
    /// directly-testable method - it needs no WPF resource loading, unlike constructing a full
    /// PumpTileViewModel (which builds a real placeholder BitmapImage from a pack:// URI).
    /// </summary>
    public static string? ResolveSnapshotStatus(int pumpNumber, PumpState state) =>
        state == PumpState.TakingPhoto ? $"Pump {pumpNumber} - Taking photo" : null;

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
