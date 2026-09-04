using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// Verifies ForecourtSerilogLogging's category allowlist (only "ElApp." SourceContexts, minus the
/// diagnostics-delivery chain's own - the recursion guard), its Serilog-to-Logger.Service level mapping,
/// its "Logging:LogLevelsToPersist" parsing (matching every other El* service's convention exactly), and
/// that ForecourtSerilogSink actually forwards a persist-worthy event to IForecourtDiagnosticsLogger.
/// </summary>
public class ForecourtSerilogLoggingTests
{
    [Theory]
    [InlineData("ElApp.Watch.Wpf.ViewModels.MainViewModel", true)]
    [InlineData("ElApp.Watch.Wpf.Services.CameraSourceService", true)]
    [InlineData("ElApp.Watch.Wpf.Services.ForecourtDiagnosticsLogger", false)] // the recursion guard
    [InlineData("ElApp.Watch.Wpf.Services.ForecourtTokenClient", false)] // in LogAsync's own call chain
    [InlineData("ElApp.Watch.Wpf.Services.ForecourtApiClient", false)] // in LogAsync's own call chain
    [InlineData("Microsoft.Hosting.Lifetime", false)] // not "ElApp."
    [InlineData("System.Net.Http.HttpClient.IForecourtDiagnosticsLogger.LogicalHandler", false)] // not "ElApp."
    public void ShouldForward_only_allows_ElApp_categories_outside_the_delivery_chain(string sourceContext, bool expected)
    {
        Assert.Equal(expected, ForecourtSerilogLogging.ShouldForward(sourceContext));
    }

    [Theory]
    [InlineData(LogEventLevel.Fatal, ForecourtLogLevel.Fatal)]
    [InlineData(LogEventLevel.Error, ForecourtLogLevel.Error)]
    [InlineData(LogEventLevel.Warning, ForecourtLogLevel.Warn)]
    [InlineData(LogEventLevel.Information, ForecourtLogLevel.Info)]
    [InlineData(LogEventLevel.Debug, ForecourtLogLevel.Debug)]
    [InlineData(LogEventLevel.Verbose, ForecourtLogLevel.Trace)]
    public void ToForecourtLogLevel_maps_every_Serilog_level(LogEventLevel input, ForecourtLogLevel expected)
    {
        Assert.Equal(expected, ForecourtSerilogLogging.ToForecourtLogLevel(input));
    }

    [Fact]
    public void ParseLogLevelsToPersist_defaults_to_Fatal_Error_Warning_when_unset()
    {
        var configuration = BuildConfiguration(logLevelsToPersist: null);

        var result = ForecourtSerilogLogging.ParseLogLevelsToPersist(configuration);

        Assert.Equal([LogEventLevel.Fatal, LogEventLevel.Error, LogEventLevel.Warning], result);
    }

    [Fact]
    public void ParseLogLevelsToPersist_returns_empty_for_none()
    {
        var configuration = BuildConfiguration(logLevelsToPersist: "none");

        var result = ForecourtSerilogLogging.ParseLogLevelsToPersist(configuration);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseLogLevelsToPersist_parses_a_comma_separated_list_case_insensitively()
    {
        var configuration = BuildConfiguration(logLevelsToPersist: "Fatal, error,WARNING,information");

        var result = ForecourtSerilogLogging.ParseLogLevelsToPersist(configuration);

        Assert.Equal(
            [LogEventLevel.Fatal, LogEventLevel.Error, LogEventLevel.Warning, LogEventLevel.Information],
            result);
    }

    [Fact]
    public async Task ForecourtSerilogSink_forwards_a_persist_worthy_ElApp_event_to_IForecourtDiagnosticsLogger()
    {
        var recording = new RecordingDiagnosticsLogger();
        var services = new ServiceCollection();
        services.AddSingleton<IForecourtDiagnosticsLogger>(recording);
        var serviceProvider = services.BuildServiceProvider();

        var levelsToPersist = ForecourtSerilogLogging.ParseLogLevelsToPersist(BuildConfiguration(logLevelsToPersist: "warning"));

        // Wired exactly as ForecourtSerilogLogging.Configure wires production: a real Serilog pipeline
        // feeding a real ForecourtSerilogSink, whose callback is ForwardAsync itself - not a hand-built
        // LogEvent - so this exercises the actual SourceContext/level-filtering path end to end.
        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(new ForecourtSerilogSink(logEvent => ForecourtSerilogLogging.ForwardAsync(logEvent, levelsToPersist, () => serviceProvider)))
            .CreateLogger();

        logger.ForContext("SourceContext", "ElApp.Watch.Wpf.ViewModels.MainViewModel").Warning("camera disconnected");

        var call = await recording.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ForecourtLogLevel.Warn, call.Level);
        Assert.Equal("ElApp.Watch.Wpf.ViewModels.MainViewModel", call.Title);
        Assert.Equal("camera disconnected", call.Message);
    }

    private static IConfiguration BuildConfiguration(string? logLevelsToPersist)
    {
        var data = new Dictionary<string, string?>();
        if (logLevelsToPersist is not null)
        {
            data["Logging:LogLevelsToPersist"] = logLevelsToPersist;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private sealed class RecordingDiagnosticsLogger : IForecourtDiagnosticsLogger
    {
        public TaskCompletionSource<(ForecourtLogLevel Level, string Title, string Message, string? MoreInfo)> Completion { get; } = new();

        public Task LogAsync(ForecourtLogLevel level, string title, string message, string? moreInfo = null, CancellationToken cancellationToken = default)
        {
            Completion.TrySetResult((level, title, message, moreInfo));
            return Task.CompletedTask;
        }
    }
}
