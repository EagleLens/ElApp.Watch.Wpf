using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services.Interface;
using ElApp.Watch.Wpf.ViewModels;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Mirrors the original MainWindow.xaml.cs's StartPump1CameraAsync/StartPumpVideoAsync +
/// TryAnalyzeThrottled/AttachPumpMonitor/PublishFrame. Detection runs on a background task at a
/// fixed cadence, decoupled from frame capture/display (which stay at the source's native rate) -
/// see AnalysisIntervalMs below for why.
/// </summary>
public sealed class CameraSourceService(
    Lazy<VehicleDetector> vehicleDetector,
    ISnapshotService snapshotService,
    IUiDispatcher dispatcher) : ICameraSourceService
{
    // Running the vehicle detector on every captured frame made the capture loop (and so
    // on-screen playback) as slow as inference itself - ~250ms/call, and shared by every
    // pump through one lock (OpenCV's dnn backend isn't safe under concurrent native calls).
    // AnalysisFps (not the video's own fps) is what the state machine's stillness/grace timers
    // are calibrated to, since that's the actual rate presence gets re-checked at.
    private const int AnalysisIntervalMs = 1000;
    private const double AnalysisFps = 1000.0 / AnalysisIntervalMs;

    private sealed class AnalysisThrottle
    {
        public readonly Stopwatch SinceLastAnalysis = Stopwatch.StartNew();
        public bool Busy;
    }

    public async Task StartLiveCameraAsync(PumpTileViewModel tile, CancellationToken token)
    {
        VideoCapture? capture = await Task.Run(() =>
        {
            for (int index = 0; index < 4; index++)
            {
                var candidate = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
                if (candidate.IsOpened())
                {
                    return candidate;
                }
                candidate.Dispose();
            }
            return null;
        }, token);

        if (token.IsCancellationRequested)
        {
            capture?.Dispose();
            return;
        }

        if (capture is null)
        {
            SetOnlineStatus(tile, online: false, "NO CAMERA");
            return;
        }

        SetOnlineStatus(tile, online: true, "LIVE");
        // Loading the vehicle-detection model is not free - keep it off the UI thread.
        await Task.Run(() => AttachPumpMonitor(tile, AnalysisFps, roi: null), token);

        try
        {
            await Task.Run(() =>
            {
                using var frame = new Mat();
                const int maxConsecutiveFailures = 30;
                int consecutiveFailures = 0;
                var throttle = new AnalysisThrottle();

                while (!token.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= maxConsecutiveFailures)
                        {
                            break;
                        }
                        continue;
                    }

                    consecutiveFailures = 0;
                    TryAnalyzeThrottled(tile, frame, throttle);
                    PublishFrame(tile, frame);
                }
            }, token);
        }
        finally
        {
            capture.Dispose();
        }

        if (!token.IsCancellationRequested)
        {
            SetOnlineStatus(tile, online: false, "OFFLINE");
        }
    }

    public async Task StartVideoFileAsync(PumpTileViewModel tile, string filePath, Rect? roi, CancellationToken token)
    {
        VideoCapture? capture = await Task.Run(() =>
        {
            if (!File.Exists(filePath))
            {
                return null;
            }
            var candidate = new VideoCapture(filePath);
            return candidate.IsOpened() ? candidate : null;
        }, token);

        if (token.IsCancellationRequested)
        {
            capture?.Dispose();
            return;
        }

        if (capture is null)
        {
            SetOnlineStatus(tile, online: false, "NO VIDEO");
            return;
        }

        SetOnlineStatus(tile, online: true, "LIVE");

        double fps = capture.Fps > 0 ? capture.Fps : 25;
        int frameDelayMs = Math.Max(1, (int)Math.Round(1000.0 / fps));
        // Loading the vehicle-detection model is not free - keep it off the UI thread.
        await Task.Run(() => AttachPumpMonitor(tile, AnalysisFps, roi), token);

        try
        {
            await Task.Run(() =>
            {
                using var frame = new Mat();
                var throttle = new AnalysisThrottle();
                while (!token.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        capture.Set(VideoCaptureProperties.PosFrames, 0);
                        continue;
                    }

                    TryAnalyzeThrottled(tile, frame, throttle);
                    PublishFrame(tile, frame);
                    Thread.Sleep(frameDelayMs);
                }
            }, token);
        }
        finally
        {
            capture.Dispose();
        }
    }

    /// <summary>
    /// Kicks off vehicle detection for this frame on a background task if enough time has
    /// passed since the last pass and none is already in flight - otherwise leaves the frame
    /// unanalyzed. This is what keeps capture/playback running at full speed regardless of how
    /// slow (or contended) detection is.
    /// </summary>
    private static void TryAnalyzeThrottled(PumpTileViewModel tile, Mat frame, AnalysisThrottle throttle)
    {
        if (throttle.Busy || throttle.SinceLastAnalysis.ElapsedMilliseconds < AnalysisIntervalMs)
        {
            return;
        }

        throttle.Busy = true;
        throttle.SinceLastAnalysis.Restart();
        Mat frameClone = frame.Clone();
        _ = Task.Run(() =>
        {
            try
            {
                tile.Monitor!.ProcessFrame(frameClone);
            }
            finally
            {
                frameClone.Dispose();
                throttle.Busy = false;
            }
        });
    }

    private void AttachPumpMonitor(PumpTileViewModel tile, double fps, Rect? roi)
    {
        var monitor = new PumpMonitor(tile.PumpId, fps, vehicleDetector.Value, roi);
        monitor.StatusChanged += (_, e) => dispatcher.BeginInvoke(() => tile.ApplyVehicleStatus(e.State));
        monitor.PhotoCaptured += (_, e) => OnPumpPhotoCaptured(tile, e.Frame);
        tile.Monitor = monitor;
    }

    private void OnPumpPhotoCaptured(PumpTileViewModel tile, Mat frame)
    {
        SnapshotResult result = snapshotService.SaveCapture(frame, tile.PumpNumber);

        string statusText = (result.Saved, result.PlateText) switch
        {
            (true, not null) => $"Photo saved - {result.PlateText}",
            (true, null) => "Photo saved (plate unclear)",
            (false, _) => "Photo captured (save failed)",
        };
        string statusIcon = result.Saved ? "📷" : "⚠";

        dispatcher.BeginInvoke(() =>
        {
            tile.SnapshotImage = result.Bitmap;
            tile.SnapshotOverlayVisible = true;
            tile.ShowTransientStatus(statusText, result.Saved ? AppBrushes.Online : AppBrushes.Offline, statusIcon);
        });
    }

    private void PublishFrame(PumpTileViewModel tile, Mat frame)
    {
        BitmapSource bitmap = frame.ToBitmapSource();
        bitmap.Freeze();
        dispatcher.BeginInvoke(() => tile.VideoSource = bitmap);
    }

    private void SetOnlineStatus(PumpTileViewModel tile, bool online, string badgeText)
    {
        dispatcher.BeginInvoke(() => tile.SetOnlineStatus(online, badgeText));
    }
}
