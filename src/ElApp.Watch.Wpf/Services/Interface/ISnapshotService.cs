using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace ElApp.Watch.Wpf.Services.Interface;

public readonly record struct SnapshotResult(BitmapSource Bitmap, bool Saved, string? PlateText, string? FilePath);

/// <summary>
/// Reads a plate from a just-captured vehicle photo and saves it to disk. Takes ownership of
/// the frame passed in (disposes it before returning).
/// </summary>
public interface ISnapshotService
{
    SnapshotResult SaveCapture(Mat frame, int pumpNumber);
}
