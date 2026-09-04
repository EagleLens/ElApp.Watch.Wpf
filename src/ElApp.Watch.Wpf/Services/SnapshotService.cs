using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Serilog;
using System.IO;
using System.Windows.Media.Imaging;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Mirrors the original MainWindow.xaml.cs's OnPumpPhotoCaptured: attempts a plate read, saves
/// the frame as a JPEG, and hands back a UI-ready bitmap - all off the UI thread; the caller
/// marshals the result onto a tile's bound properties.
/// </summary>
public sealed class SnapshotService(Lazy<PlateReader> plateReader, IOptions<SnapshotOptions> snapshotOptions) : ISnapshotService
{
    public SnapshotResult SaveCapture(Mat frame, int pumpNumber)
    {
        using (frame)
        {
            Log.Verbose("Verbose:Application Starting");
            Log.Information("Information:Application Starting");
            Log.Debug("Debug:Application Starting");
            Log.Warning("Warning:Application Starting");
            Log.Error("Error:Application Starting");
            Log.Fatal("Fatal:Application Starting");
            string? plateText = null;
            try
            {
                plateText = plateReader.Value.ReadPlate(frame);
            }
            catch (OnnxRuntimeException)
            {
                // best-effort - the photo itself still gets saved and shown even if plate reading fails
            }

            string snapshotDir = Path.Combine(AppContext.BaseDirectory, snapshotOptions.Value.OutputFolderName);
            bool saved = false;
            try
            {
                Directory.CreateDirectory(snapshotDir);
                string plateSuffix = plateText is not null ? $"_{plateText}" : string.Empty;
                string filePath = Path.Combine(snapshotDir, $"pump{pumpNumber}{plateSuffix}_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
                Cv2.ImWrite(filePath, frame);
                saved = true;
            }
            catch (IOException)
            {
                // best-effort disk persistence; the in-memory snapshot below still shows regardless
            }

            BitmapSource bitmap = frame.ToBitmapSource();
            bitmap.Freeze();
            return new SnapshotResult(bitmap, saved, plateText);
        }
    }
}
