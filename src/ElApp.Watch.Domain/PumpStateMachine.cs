namespace ElApp.Watch.Domain;

/// <summary>
/// Per-pump vehicle stop-detection state machine. Pure logic - it consumes a
/// simple presence/motion signal for each frame and emits status-changed /
/// photo-captured events. It has no image-processing dependency, so it can be
/// unit tested and driven by any motion source (OpenCV, a mock, replayed
/// fixtures, etc). <typeparamref name="TFrame"/> is whatever frame
/// representation the caller wants handed back on capture (e.g. an OpenCV Mat).
///
/// Exactly one photo fires per stop, because the capture only happens on the
/// VehicleStopping -> TakingPhoto edge - reaching that edge again requires
/// passing back through Empty and VehicleComing first, so "already captured"
/// resets only when the pump goes back to empty, never on a timer.
/// </summary>
public sealed class PumpStateMachine<TFrame>
{
    private readonly string _pumpId;
    private readonly int _requiredStillFrames;
    private readonly int _requiredGraceFrames;

    private int _stillFrameCount;
    private int _absentFrameCount;

    /// <param name="pumpId">Identifier included on every emitted event.</param>
    /// <param name="fps">Approximate source frame rate, used to convert the duration parameters below into frame counts.</param>
    /// <param name="stopConfirmSeconds">How long the vehicle must hold still before it's confirmed stopped and a photo is taken.</param>
    /// <param name="graceSeconds">How long the ROI must read "empty" before a presence-loss is trusted, so a person briefly walking through doesn't reset the pump.</param>
    public PumpStateMachine(string pumpId, double fps, double stopConfirmSeconds = 2.5, double graceSeconds = 1.5)
    {
        _pumpId = pumpId;
        // Only fall back for a truly invalid fps (some capture sources report 0) - a legitimate
        // low rate, like the throttled ~1fps vehicle-detection cadence, must be honored as-is or
        // these thresholds silently balloon back up to 15fps-sized frame counts.
        double effectiveFps = fps > 0 ? fps : 15;
        _requiredStillFrames = Math.Max(1, (int)Math.Round(effectiveFps * stopConfirmSeconds));
        _requiredGraceFrames = Math.Max(1, (int)Math.Round(effectiveFps * graceSeconds));
    }

    public PumpState State { get; private set; } = PumpState.Empty;

    public event EventHandler<PumpStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<PhotoCapturedEventArgs<TFrame>>? PhotoCaptured;

    /// <summary>
    /// Advance the machine by one frame's worth of signal.
    /// </summary>
    /// <param name="isPresent">Whether the ROI currently reads as occupied.</param>
    /// <param name="isMoving">Whether the occupant is currently moving/settling (ignored once parked).</param>
    /// <param name="frameProvider">Lazily invoked only if this call actually triggers a photo capture.</param>
    public void Update(bool isPresent, bool isMoving, Func<TFrame> frameProvider)
    {
        switch (State)
        {
            case PumpState.Empty:
                if (isPresent)
                {
                    _stillFrameCount = 0;
                    _absentFrameCount = 0;
                    TransitionTo(PumpState.VehicleComing);
                }
                break;

            case PumpState.VehicleComing:
                if (!isPresent)
                {
                    if (++_absentFrameCount >= _requiredGraceFrames)
                    {
                        TransitionTo(PumpState.Empty);
                    }
                }
                else
                {
                    _absentFrameCount = 0;
                    if (isMoving)
                    {
                        _stillFrameCount = 0;
                    }
                    else
                    {
                        _stillFrameCount = 1;
                        TransitionTo(PumpState.VehicleStopping);
                    }
                }
                break;

            case PumpState.VehicleStopping:
                if (!isPresent)
                {
                    if (++_absentFrameCount >= _requiredGraceFrames)
                    {
                        TransitionTo(PumpState.Empty);
                    }
                }
                else
                {
                    _absentFrameCount = 0;
                    if (isMoving)
                    {
                        // Motion picked back up before settling - it was still pulling in.
                        _stillFrameCount = 0;
                        TransitionTo(PumpState.VehicleComing);
                    }
                    else if (++_stillFrameCount >= _requiredStillFrames)
                    {
                        TransitionTo(PumpState.TakingPhoto);
                        PhotoCaptured?.Invoke(this, new PhotoCapturedEventArgs<TFrame>
                        {
                            PumpId = _pumpId,
                            Frame = frameProvider(),
                            TimestampUtc = DateTime.UtcNow
                        });
                    }
                }
                break;

            case PumpState.TakingPhoto:
                // One-tick pulse: the photo already fired on the transition into this
                // state, so immediately settle into the parked "stopped" state. From
                // here on, motion/jitter while parked is ignored entirely - only a
                // sustained absence (below) clears the pump.
                _absentFrameCount = isPresent ? 0 : 1;
                TransitionTo(PumpState.VehicleStopped);
                break;

            case PumpState.VehicleStopped:
                if (isPresent)
                {
                    _absentFrameCount = 0;
                }
                else if (++_absentFrameCount >= _requiredGraceFrames)
                {
                    _stillFrameCount = 0;
                    _absentFrameCount = 0;
                    TransitionTo(PumpState.Empty);
                }
                break;
        }
    }

    private void TransitionTo(PumpState newState)
    {
        State = newState;
        StatusChanged?.Invoke(this, new PumpStatusChangedEventArgs
        {
            PumpId = _pumpId,
            State = newState,
            TimestampUtc = DateTime.UtcNow
        });
    }
}
