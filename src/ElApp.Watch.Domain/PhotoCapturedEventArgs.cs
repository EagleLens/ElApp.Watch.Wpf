namespace ElApp.Watch.Domain;

public sealed class PhotoCapturedEventArgs<TFrame> : EventArgs
{
    public required string PumpId { get; init; }
    public required TFrame Frame { get; init; }
    public required DateTime TimestampUtc { get; init; }
}
