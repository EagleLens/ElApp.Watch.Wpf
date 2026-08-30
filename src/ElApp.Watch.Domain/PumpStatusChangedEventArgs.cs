namespace ElApp.Watch.Domain;

public sealed class PumpStatusChangedEventArgs : EventArgs
{
    public required string PumpId { get; init; }
    public required PumpState State { get; init; }
    public required DateTime TimestampUtc { get; init; }
}
