namespace ElApp.Watch.Wpf.Services;

/// <summary>Bound from the "Vision" section of appsettings.json.</summary>
public sealed class VisionOptions
{
    public const string SectionName = "Vision";

    public required string VehicleDetectorModelPath { get; init; }
    public required string PlateDetectorModelPath { get; init; }
    public required string PlateOcrModelPath { get; init; }
}
