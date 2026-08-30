using OpenCvSharp;

namespace ElApp.Watch.Vision;

/// <summary>
/// Determines per-frame vehicle presence/motion for a fixed camera region by running a
/// <see cref="VehicleDetector"/> and tracking the resulting box across frames - it feeds a
/// <see cref="ElApp.Watch.Domain.PumpStateMachine{TFrame}"/>, which is where the actual stop logic
/// lives. Because presence comes from real object classification rather than a motion-blob
/// heuristic, this needs no per-camera calibration: the same thresholds hold regardless of
/// resolution, camera distance, or how much of the frame the vehicle fills.
/// </summary>
public sealed class RoiMotionAnalyzer
{
    // How far the tracked vehicle's box center may drift between frames, as a fraction of the
    // box's own diagonal, before it still counts as "moving". Scaling by the box's own size
    // (rather than a fixed pixel count) is what keeps this resolution/distance-agnostic.
    private const double MovementFraction = 0.04;

    private readonly Rect? _roi;
    private readonly VehicleDetector _detector;
    private Rect? _previousVehicleBox;

    /// <param name="detector">
    /// Shared vehicle detector - OpenCV's dnn backend was not stable with multiple independent
    /// Net instances running (even serialized through a lock, separate Net objects sharing the
    /// process crashed it), so every pump's analyzer uses the same one instance.
    /// </param>
    /// <param name="roi">Optional region to restrict detection to, in source-frame pixel coordinates - useful when a single camera sees more than one pump bay. Null analyzes the whole frame.</param>
    public RoiMotionAnalyzer(VehicleDetector detector, Rect? roi = null)
    {
        _detector = detector;
        _roi = roi;
    }

    /// <summary>
    /// Analyze one frame. IsPresent is true only when a car/motorcycle/bus/truck is detected in
    /// the region. IsMoving compares the detected box to the previous frame's, so a vehicle that
    /// has stopped reads as still even while background traffic or a pedestrian moves nearby.
    /// </summary>
    public (bool IsPresent, bool IsMoving) Analyze(Mat frameBgr)
    {
        // Only the cropped case owns a new Mat and needs disposing - when _roi is null,
        // region aliases the caller's frame, which the caller still owns.
        using Mat? croppedRegion = _roi is { } roi ? new Mat(frameBgr, roi) : null;
        Mat region = croppedRegion ?? frameBgr;

        VehicleDetection? detection = _detector.DetectLargestVehicle(region);
        if (detection is null)
        {
            _previousVehicleBox = null;
            return (false, false);
        }

        Rect box = detection.Value.Box;
        bool isMoving = true;
        if (_previousVehicleBox is { } previous)
        {
            double dx = (box.X + box.Width / 2.0) - (previous.X + previous.Width / 2.0);
            double dy = (box.Y + box.Height / 2.0) - (previous.Y + previous.Height / 2.0);
            double centerDrift = Math.Sqrt(dx * dx + dy * dy);
            double diagonal = Math.Sqrt((double)box.Width * box.Width + (double)box.Height * box.Height);
            isMoving = diagonal <= 0 || centerDrift / diagonal > MovementFraction;
        }
        _previousVehicleBox = box;

        return (true, isMoving);
    }
}
