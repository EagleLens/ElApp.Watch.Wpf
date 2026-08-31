namespace ElApp.Watch.Wpf.Services;

/// <summary>Bound from the "Snapshots" section of appsettings.json.</summary>
public sealed class SnapshotOptions
{
    public const string SectionName = "Snapshots";

    public required string OutputFolderName { get; init; }
}
