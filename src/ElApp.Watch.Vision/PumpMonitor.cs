using ElApp.Watch.Domain;
using OpenCvSharp;

namespace ElApp.Watch.Vision;

/// <summary>
/// Ties a fixed-ROI <see cref="RoiMotionAnalyzer"/> to a <see cref="PumpStateMachine{TFrame}"/>
/// for one pump. Call <see cref="ProcessFrame"/> once per captured frame; subscribe to
/// <see cref="StatusChanged"/> to drive a live UI indicator and to <see cref="PhotoCaptured"/>
/// to handle the single capture per vehicle stop. The Mat handed back by PhotoCaptured is a
/// clone owned by the subscriber - dispose it once you're done with it.
/// </summary>
public sealed class PumpMonitor
{
    private readonly RoiMotionAnalyzer _analyzer;
    private readonly PumpStateMachine<Mat> _stateMachine;

    /// <param name="pumpId">Identifier included on every emitted event.</param>
    /// <param name="fps">Approximate source frame rate.</param>
    /// <param name="detector">Shared vehicle detector - see <see cref="RoiMotionAnalyzer"/> for why this is shared rather than owned per-pump.</param>
    /// <param name="roi">Fixed pump camera region in source-frame pixel coordinates. Null monitors the whole frame.</param>
    public PumpMonitor(string pumpId, double fps, VehicleDetector detector, Rect? roi = null)
    {
        PumpId = pumpId;
        _analyzer = new RoiMotionAnalyzer(detector, roi);
        _stateMachine = new PumpStateMachine<Mat>(pumpId, fps);
        _stateMachine.StatusChanged += (_, e) => StatusChanged?.Invoke(this, e);
        _stateMachine.PhotoCaptured += (_, e) => PhotoCaptured?.Invoke(this, e);
    }

    public string PumpId { get; }

    public PumpState State => _stateMachine.State;

    public event EventHandler<PumpStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<PhotoCapturedEventArgs<Mat>>? PhotoCaptured;

    public void ProcessFrame(Mat frameBgr)
    {
        (bool isPresent, bool isMoving) = _analyzer.Analyze(frameBgr);
        _stateMachine.Update(isPresent, isMoving, frameBgr.Clone);
    }
}
