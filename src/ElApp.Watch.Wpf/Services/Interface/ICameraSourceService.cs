using ElApp.Watch.Wpf.ViewModels;

namespace ElApp.Watch.Wpf.Services.Interface;

/// <summary>
/// Drives one pump tile's capture loop: opens a live camera or a looping video file, publishes
/// frames to the tile, and runs throttled vehicle-stop detection against them.
/// </summary>
public interface ICameraSourceService
{
    Task StartLiveCameraAsync(PumpTileViewModel tile, CancellationToken token);

    Task StartVideoFileAsync(PumpTileViewModel tile, string filePath, OpenCvSharp.Rect? roi, CancellationToken token);
}
