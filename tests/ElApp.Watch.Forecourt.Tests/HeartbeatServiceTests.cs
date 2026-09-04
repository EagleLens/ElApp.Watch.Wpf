using ElApp.Watch.Forecourt;
using Serilog.Events;
using Xunit;

namespace ElApp.Watch.Forecourt.Tests;

/// <summary>
/// Verifies HeartbeatService.ResolveInterval's clamping (a misconfigured near-zero/negative
/// IntervalMinutes must not flood Logger.Service) and that running the service actually logs a Warning
/// via plain Serilog.Log - the same mechanism ForecourtSerilogLogging forwards from - on each tick.
/// </summary>
public class HeartbeatServiceTests
{
    [Theory]
    [InlineData(5, 5)]
    [InlineData(0.1, 0.1)] // exactly the floor
    [InlineData(0.01, HeartbeatService.MinimumIntervalMinutes)] // below the floor - clamped up
    [InlineData(0, HeartbeatService.MinimumIntervalMinutes)] // misconfigured to zero - clamped up
    [InlineData(-5, HeartbeatService.MinimumIntervalMinutes)] // misconfigured negative - clamped up
    public void ResolveInterval_clamps_to_at_least_the_minimum(double configuredMinutes, double expectedMinutes)
    {
        var interval = HeartbeatService.ResolveInterval(new HeartbeatOptions { IntervalMinutes = configuredMinutes });

        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), interval);
    }

    [Fact]
    public async Task ExecuteAsync_logs_a_Warning_via_Serilog_on_each_tick()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<LogEvent>();
        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new ForecourtSerilogSink(logEvent =>
            {
                events.Enqueue(logEvent);
                return Task.CompletedTask;
            }))
            .CreateLogger();
        var previous = Serilog.Log.Logger;
        Serilog.Log.Logger = logger;

        try
        {
            using var service = new HeartbeatService(Microsoft.Extensions.Options.Options.Create(
                new HeartbeatOptions { IntervalMinutes = HeartbeatService.MinimumIntervalMinutes }));

            // BackgroundService.StartAsync returns as soon as ExecuteAsync starts running, not when its
            // loop finishes - only StopAsync actually awaits (and cancels) that loop.
            await service.StartAsync(CancellationToken.None);
            await WaitUntilAsync(() => !events.IsEmpty, TimeSpan.FromSeconds(15));
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            Serilog.Log.Logger = previous;
        }

        Assert.Contains(events, e => e.Level == LogEventLevel.Warning && e.RenderMessage().Contains("heartbeat", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }
}
