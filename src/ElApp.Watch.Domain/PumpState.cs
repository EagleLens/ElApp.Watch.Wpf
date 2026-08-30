namespace ElApp.Watch.Domain;

/// <summary>
/// Live vehicle-presence status for a single pump ROI, in the order a normal
/// stop cycle passes through them (Empty -> Coming -> Stopping -> TakingPhoto
/// -> Stopped -> back to Empty).
/// </summary>
public enum PumpState
{
    Empty,
    VehicleComing,
    VehicleStopping,
    TakingPhoto,
    VehicleStopped
}
