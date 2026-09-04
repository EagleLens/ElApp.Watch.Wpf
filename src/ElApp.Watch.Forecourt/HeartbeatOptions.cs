namespace ElApp.Watch.Forecourt;

/// <summary>Bound from the "Heartbeat" section of appsettings.json.</summary>
public sealed class HeartbeatOptions
{
    public const string SectionName = "Heartbeat";

    /// <summary>
    /// How often <see cref="HeartbeatService"/> logs a heartbeat, in minutes. Clamped to at least
    /// <see cref="HeartbeatService.MinimumIntervalMinutes"/> - a misconfigured near-zero value would
    /// otherwise flood Logger.Service.
    /// </summary>
    public double IntervalMinutes { get; init; } = 5;
}
