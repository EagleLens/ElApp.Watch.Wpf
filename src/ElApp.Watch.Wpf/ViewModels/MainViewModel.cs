using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services;

namespace ElApp.Watch.Wpf.ViewModels;

/// <summary>
/// Mirrors the original MainWindow.xaml.cs's top-level state: the randomized pump-tile grid
/// (BuildCameraTiles), the clock, the online-camera count, and the label-visibility toggle.
/// Owns the capture-loop lifetime for the 4 dynamic tiles (pump1's live camera, pumps 2-4's
/// sample videos) via <see cref="ICameraSourceService"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICameraSourceService _cameraSourceService;
    private readonly Lazy<VehicleDetector> _vehicleDetector;
    private readonly Lazy<PlateReader> _plateReader;
    private readonly CancellationTokenSource _cameraCts = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly int _pumpCount = Math.Max(4, Random.Shared.Next(1, 9));

    public MainViewModel(ICameraSourceService cameraSourceService, Lazy<VehicleDetector> vehicleDetector, Lazy<PlateReader> plateReader)
    {
        _cameraSourceService = cameraSourceService;
        _vehicleDetector = vehicleDetector;
        _plateReader = plateReader;

        Tiles = BuildTiles(_pumpCount);
        foreach (PumpTileViewModel tile in Tiles)
        {
            tile.PropertyChanged += OnTilePropertyChanged;
        }
        UpdateOnlineCountText();

        // Matches BuildCameraTiles's original grid-sizing formula exactly - UniformGrid's own
        // auto-layout (when Rows/Columns are left at 0) uses a different algorithm and could
        // produce a different row/column split for the same tile count.
        GridColumns = (int)Math.Ceiling(Math.Sqrt(_pumpCount));
        GridRows = (int)Math.Ceiling(_pumpCount / (double)GridColumns);

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clockTimer.Start();
        ClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    }

    public ObservableCollection<PumpTileViewModel> Tiles { get; }

    public int GridColumns { get; private set; }
    public int GridRows { get; private set; }

    [ObservableProperty]
    private string _clockText = "--:--:--";

    [ObservableProperty]
    private string _onlineCountText = "Detecting cameras...";

    [ObservableProperty]
    private Brush _onlineCountBrush = AppBrushes.Online;

    [ObservableProperty]
    private bool _showStatusLabels = true;

    /// <summary>Kicks off pump1's live camera and pumps 2-4's sample videos. Call once after the view has attached.</summary>
    public void Start(string assetsBaseDirectory)
    {
        PumpTileViewModel pump1 = Tiles[0];
        _ = _cameraSourceService.StartLiveCameraAsync(pump1, _cameraCts.Token);

        (int pumpNumber, string fileName)[] sampleVideos =
        [
            (2, "cctv_multi_vehicle_test.mp4"),
            (3, "cctv_multi_vehicle_test1.mp4"),
            (4, "cctv_multi_vehicle_test2.mp4"),
        ];
        foreach ((int pumpNumber, string fileName) in sampleVideos)
        {
            PumpTileViewModel? tile = Tiles.FirstOrDefault(t => t.PumpNumber == pumpNumber);
            if (tile is not null)
            {
                string videoPath = Path.Combine(assetsBaseDirectory, fileName);
                _ = _cameraSourceService.StartVideoFileAsync(tile, videoPath, roi: null, _cameraCts.Token);
            }
        }
    }

    [RelayCommand]
    private void ToggleLabels()
    {
        ShowStatusLabels = !ShowStatusLabels;
        foreach (PumpTileViewModel tile in Tiles)
        {
            tile.SetShowStatusLabels(ShowStatusLabels);
        }
    }

    private static ObservableCollection<PumpTileViewModel> BuildTiles(int count)
    {
        var tiles = new ObservableCollection<PumpTileViewModel>();
        for (int i = 0; i < count; i++)
        {
            bool isDynamicTile = i <= 3;
            tiles.Add(new PumpTileViewModel(pumpNumber: i + 1, isDynamic: isDynamicTile));
        }
        return tiles;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PumpTileViewModel.Online))
        {
            UpdateOnlineCountText();
        }
    }

    private void UpdateOnlineCountText()
    {
        int alwaysOnlineFillerCount = Tiles.Count(t => !t.IsDynamic);
        int dynamicOnlineCount = Tiles.Count(t => t.IsDynamic && t.Online);
        int online = alwaysOnlineFillerCount + dynamicOnlineCount;

        OnlineCountText = $"{online} / {Tiles.Count} Cameras Online";
        OnlineCountBrush = online == Tiles.Count ? AppBrushes.Online : AppBrushes.Offline;
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        _cameraCts.Cancel();
        _cameraCts.Dispose();
        _clockTimer.Stop();

        if (_vehicleDetector.IsValueCreated)
        {
            _vehicleDetector.Value.Dispose();
        }
        if (_plateReader.IsValueCreated)
        {
            _plateReader.Value.Dispose();
        }
    }
}
