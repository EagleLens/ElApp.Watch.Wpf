using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Events;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// Verifies ForecourtSerilogLogging forwards every source except HttpClientFactory's own request-logging
/// for the diagnostics-delivery HttpClients (the recursion guard), its Serilog-to-Logger.Service level
/// mapping, its "Logging:LogLevelsToPersist" parsing (matching every other El* service's convention
/// exactly), and that ForecourtSerilogSink actually forwards a persist-worthy event - including a plain
/// static Serilog.Log call with no SourceContext at all - to IForecourtDiagnosticsLogger.
/// </summary>
public class ForecourtSerilogLoggingTests
{
    [Theory]
    [InlineData(null, true)] // a plain static Serilog.Log call with no attached context - always forwarded
    [InlineData("ElApp.Watch.Wpf.ViewModels.MainViewModel", true)]
    [InlineData("Microsoft.Hosting.Lifetime", true)] // no source filtering beyond the exclusions below
    [InlineData("System.Net.Http.HttpClient.IForecourtDiagnosticsLogger.LogicalHandler", false)] // the recursion guard
    [InlineData("System.Net.Http.HttpClient.IForecourtDiagnosticsLogger.ClientHandler", false)] // the recursion guard
    [InlineData("System.Net.Http.HttpClient.IForecourtTokenClient.LogicalHandler", false)] // in LogAsync's own call chain
    [InlineData("System.Net.Http.HttpClient.IForecourtApiClient.LogicalHandler", false)] // in LogAsync's own call chain
    public void ShouldForward_excludes_only_the_diagnostics_delivery_HttpClients_own_logging(string? sourceContext, bool expected)
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
    public async Task ForecourtSerilogSink_forwards_a_persist_worthy_event_with_a_SourceContext()
    {
        var (recording, logger) = CreateEndToEndSink(logLevelsToPersist: "warning");

        logger.ForContext("SourceContext", "ElApp.Watch.Wpf.ViewModels.MainViewModel").Warning("camera disconnected");

        var call = await recording.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ForecourtLogLevel.Warn, call.Level);
        Assert.Equal("ElApp.Watch.Wpf.ViewModels.MainViewModel", call.Title);
        Assert.Equal("camera disconnected", call.Message);
    }

    [Fact]
    public async Task ForecourtSerilogSink_forwards_a_plain_static_Log_call_with_no_SourceContext()
    {
        // This is exactly the pattern the platform's API/MVC services use - plain Serilog.Log.Warning(...),
        // no injected ILogger<T>, no attached context - and it must work with zero extra ceremony.
        var (recording, logger) = CreateEndToEndSink(logLevelsToPersist: "warning");

        logger.Warning("camera disconnected");

        var call = await recording.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(ForecourtLogLevel.Warn, call.Level);
        Assert.Equal("Serilog", call.Title); // no SourceContext to use - falls back to a generic title
        Assert.Equal("camera disconnected", call.Message);
    }

    /// <summary>
    /// Wires a real Serilog pipeline feeding a real ForecourtSerilogSink, whose callback is
    /// ForwardAsync itself - not a hand-built LogEvent - exactly as ForecourtSerilogLogging.Configure
    /// wires production, so these tests exercise the actual filtering path end to end.
    /// </summary>
    private static (RecordingDiagnosticsLogger Recording, Serilog.ILogger Logger) CreateEndToEndSink(string logLevelsToPersist)
    {
        var recording = new RecordingDiagnosticsLogger();
        var services = new ServiceCollection();
        services.AddSingleton<IForecourtDiagnosticsLogger>(recording);
        var serviceProvider = services.BuildServiceProvider();

        var levelsToPersist = ForecourtSerilogLogging.ParseLogLevelsToPersist(BuildConfiguration(logLevelsToPersist));

        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .WriteTo.Sink(new ForecourtSerilogSink(logEvent => ForecourtSerilogLogging.ForwardAsync(logEvent, levelsToPersist, () => serviceProvider)))
            .CreateLogger();

        return (recording, logger);
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
