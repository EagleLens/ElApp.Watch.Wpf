using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Logs a Warning-level heartbeat on a configurable interval (appsettings' Heartbeat:IntervalMinutes),
/// so an unattended station that's simply gone quiet - no crash, no error, just stopped reporting - is
/// still distinguishable from one that's fine but idle. Uses plain Serilog.Log.Warning(...), same as the
/// rest of this app - ForecourtSerilogLogging's sink picks it up and forwards it to Logger.Service (public
/// or private, decided the same way as every other forwarded log) with no further wiring needed here.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    /// <summary>Floor for a misconfigured near-zero/negative IntervalMinutes, so a typo can't flood Logger.Service.</summary>
    public const double MinimumIntervalMinutes = 1.0 / 60.0; // 1 second

    private readonly HeartbeatOptions _options;

    public HeartbeatService(IOptions<HeartbeatOptions> options)
    {
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ResolveInterval(_options));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            Log.Warning("[HeartBeat] Forecourt Watch heartbeat - {MachineName} still running.", Environment.MachineName);
        }
    }

    public static TimeSpan ResolveInterval(HeartbeatOptions options) =>
        TimeSpan.FromMinutes(Math.Max(options.IntervalMinutes, MinimumIntervalMinutes));
}
