using ElApp.Watch.Forecourt;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services.Interface;
using ElApp.Watch.Wpf.ViewModels;
using Microsoft.Extensions.Options;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows.Media.Imaging;

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
    IUiDispatcher dispatcher,
    IForecourtApiClient forecourtApiClient,
    IOptions<ImageProcessingOptions> imageProcessingOptions) : ICameraSourceService
{
    // Running the vehicle detector on every captured frame made the capture loop (and so
    // on-screen playback) as slow as inference itself - ~250ms/call, and shared by every
    // pump through one lock (OpenCV's dnn backend isn't safe under concurrent native calls).
    // AnalysisFps (not the video's own fps) is what the state machine's stillness/grace timers
    // are calibrated to, since that's the actual rate presence gets re-checked at.
    private const int AnalysisIntervalMs = 1000;
    private const double AnalysisFps = 1000.0 / AnalysisIntervalMs;

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

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
            tile.ShowSnapshot(result.Bitmap);
            tile.ShowTransientStatus(statusText, result.Saved ? AppBrushes.Online : AppBrushes.Offline, statusIcon);

            // Shows the loading state (UK-plate readout + spinner) the instant the request is about to
            // go out, not whenever it happens to come back - see BeginResultCapture.
            if (result.Saved && result.FilePath is not null)
            {
                tile.BeginResultCapture(result.PlateText);
            }
        });

        // Fire-and-forget: the capture pipeline (and the UI update above) must not wait on a network
        // call. tile.SetSnapshotResult(...) lands whenever the response actually comes back.
        if (result.Saved && result.FilePath is not null)
        {
            _ = ProcessSavedImageAsync(tile, result.FilePath, result.PlateText);
        }
    }

    /// <summary>
    /// Posts a just-saved snapshot to ElApp.MainExternal.Service's ProcessImage endpoint and applies its
    /// result (Valid/Invalid/Warning) as the tile's snapshot-overlay indicator (green/red/amber).
    /// Best-effort: any failure (auth, network, non-success status, an unparsable/empty response) is
    /// logged via Serilog - which ForecourtSerilogLogging already forwards to Logger.Service - and
    /// simply leaves the indicator at its neutral "pending" color rather than throwing or blocking
    /// anything else.
    /// </summary>
    private async Task ProcessSavedImageAsync(PumpTileViewModel tile, string filePath, string? plateText)
    {
        try
        {
            byte[] fileContent = await File.ReadAllBytesAsync(filePath);
            string reg = Uri.EscapeDataString(plateText ?? "unknown");
            string requestUri = $"{imageProcessingOptions.Value.ProcessImageEndpoint}?reg={reg}";

            using var response = await forecourtApiClient.PostFileAsync(requestUri, fileContent, Path.GetFileName(filePath), "image/jpeg");
            string responseBody = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Process-image call for {PumpId} rejected by server: {StatusCode} {Body}", tile.PumpId, response.StatusCode, responseBody);
                return;
            }

            // System.Text.Json's default JsonSerializerOptions is case-sensitive - the server's JSON
            // keys are lowercase ("isSuccess"/"data"/"result"/...), our C# properties are PascalCase.
            var body = JsonSerializer.Deserialize<ProcessImageResponse>(responseBody, CaseInsensitiveJson);

            // A 200 with isSuccess=false (e.g. MainExternal's downstream call to Main.Service failed)
            // still carries a "data" object - just an ApiErrorViewModel-shaped one, not a
            // VehicleImageResultData one. Checking only "is data null" let that silently masquerade as
            // an empty/unmapped result instead of the actual failure it is.
            if (body is null || !body.IsSuccess)
            {
                Log.Warning("Process-image call for {PumpId} returned isSuccess=false: {Body}", tile.PumpId, responseBody);
                return;
            }

            VehicleImageResultData? data = body.Data;
            if (data is null)
            {
                Log.Warning("Process-image call for {PumpId} succeeded but returned no usable result: {Body}", tile.PumpId, responseBody);
                return;
            }

            // Logged unconditionally (not just on failure) - Result silently not mapping to a known
            // Valid/Invalid/Warning value (e.g. left at its unset default) looks identical to success
            // from everywhere else in this method, so this is the only place that would ever surface it.
            Log.Information(
                "Process-image result for {PumpId}: Reg={Reg}, Result={Result}, Warnings={Warnings}, Remarks={Remarks}",
                tile.PumpId, data.Reg, data.Result, data.Warnings, data.Remarks);

            dispatcher.BeginInvoke(() => tile.SetSnapshotResult(data));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to process the saved image for {PumpId}.", tile.PumpId);
        }
    }

    private void PublishFrame(PumpTileViewModel tile, Mat frame)
    {
        BitmapSource bitmap = frame.ToBitmapSource();
        bitmap.Freeze();
        dispatcher.BeginInvoke(() => tile.SetVideoFrame(bitmap));
    }

    private void SetOnlineStatus(PumpTileViewModel tile, bool online, string badgeText)
    {
        dispatcher.BeginInvoke(() => tile.SetOnlineStatus(online, badgeText));
    }
}
