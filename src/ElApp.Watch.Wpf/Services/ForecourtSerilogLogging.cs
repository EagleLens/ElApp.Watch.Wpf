using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Mirrors the platform-wide Serilog + Fc.Common.Logger.FcCommonSink pattern (see e.g.
/// ElApp.AuthService.Web's Mvc/Configurations/SerilogAndFcLogger.cs, ElApp.MainExternal.Service's
/// equivalent) - every service already sets Serilog as the app's logging backend, filters events through
/// an appsettings "Logging:LogLevelsToPersist" allowlist, and forwards the survivors to Logger.Service via
/// a custom ILogEventSink. Because Serilog replaces the ILoggerFactory outright, any existing or future
/// ILogger&lt;T&gt; call anywhere in the app's own code is automatically covered - no call site needs to
/// know this pipeline exists.
///
/// This differs from the platform pattern in one deliberate way: Fc.Common.Logger.FcLogger.LogSeriLog
/// always POSTs using a dedicated service-identity client_credentials token (every MVC/API service has
/// its own fixed logging credentials). This app has no such identity - the only credential it has is the
/// customer's forecourt client_credentials, which may or may not currently authenticate (see the whole
/// public/private design in ForecourtDiagnosticsLogger). So instead of posting directly, the forwarding
/// callback routes through IForecourtDiagnosticsLogger.LogAsync, which already makes that public/private
/// decision - keeping this Serilog integration and the rest of the diagnostics pipeline as one system
/// instead of two independent ones.
/// </summary>
public static class ForecourtSerilogLogging
{
    /// <summary>
    /// Configures Serilog as the process's logger (Serilog.Log.Logger) and wires it into the host via
    /// AddSerilog. Must be called before HostApplicationBuilder.Build() (Serilog convention - see every
    /// other service's Program.cs), so <paramref name="serviceProvider"/> is a lazy accessor: the sink's
    /// forwarding callback only invokes it once an actual log event needs forwarding, by which point
    /// Build()/Start() have long since completed.
    /// </summary>
    public static void Configure(HostApplicationBuilder builder, Func<IServiceProvider?> serviceProvider)
    {
        var logLevelsToPersist = ParseLogLevelsToPersist(builder.Configuration);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {SourceContext}{NewLine}{Message:lj}{NewLine}{Exception}{NewLine}",
                theme: AnsiConsoleTheme.Code)
            .WriteTo.Sink(new ForecourtSerilogSink(logEvent => ForwardAsync(logEvent, logLevelsToPersist, serviceProvider)))
            .CreateLogger();

        builder.Services.AddSerilog(dispose: true);
    }

    /// <summary>
    /// Only "ElApp." SourceContexts are ever forwarded - never third-party/framework noise - and the
    /// diagnostics-delivery chain's own categories are explicitly excluded even though they're "ElApp."
    /// too: without that, a log-delivery HTTP call could itself produce a log, forwarding which triggers
    /// another delivery attempt, recursing without end. ForecourtTokenClient/ForecourtApiClient don't log
    /// anything today, but both are in IForecourtDiagnosticsLogger.LogAsync's own call chain, so excluding
    /// them is cheap insurance against reintroducing that recursion the moment either one gains a log call.
    /// </summary>
    public static bool ShouldForward(string sourceContext) =>
        sourceContext.StartsWith("ElApp.", StringComparison.Ordinal) && !ExcludedCategories.Contains(sourceContext);

    private static readonly string[] ExcludedCategories =
    [
        typeof(ForecourtDiagnosticsLogger).FullName!,
        typeof(ForecourtTokenClient).FullName!,
        typeof(ForecourtApiClient).FullName!,
    ];

    public static async Task ForwardAsync(LogEvent logEvent, IReadOnlyCollection<LogEventLevel> logLevelsToPersist, Func<IServiceProvider?> serviceProvider)
    {
        try
        {
            if (!logLevelsToPersist.Contains(logEvent.Level))
            {
                return;
            }

            if (!TryGetSourceContext(logEvent, out var sourceContext) || !ShouldForward(sourceContext))
            {
                return;
            }

            var diagnosticsLogger = serviceProvider()?.GetService<IForecourtDiagnosticsLogger>();
            if (diagnosticsLogger is null)
            {
                return;
            }

            await diagnosticsLogger.LogAsync(
                ToForecourtLogLevel(logEvent.Level),
                sourceContext,
                logEvent.RenderMessage(),
                logEvent.Exception?.ToString());
        }
        catch
        {
            // Best-effort telemetry - forwarding a log must never itself throw/crash the app.
        }
    }

    private static bool TryGetSourceContext(LogEvent logEvent, out string sourceContext)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context })
        {
            sourceContext = context;
            return true;
        }

        sourceContext = string.Empty;
        return false;
    }

    /// <summary>Matches Fc.Common.Logger.FcLogger.ConvertLogLevel's exact Serilog-to-Logger.Service mapping.</summary>
    public static ForecourtLogLevel ToForecourtLogLevel(LogEventLevel logLevel) => logLevel switch
    {
        LogEventLevel.Fatal => ForecourtLogLevel.Fatal,
        LogEventLevel.Error => ForecourtLogLevel.Error,
        LogEventLevel.Warning => ForecourtLogLevel.Warn,
        LogEventLevel.Information => ForecourtLogLevel.Info,
        LogEventLevel.Debug => ForecourtLogLevel.Debug,
        LogEventLevel.Verbose => ForecourtLogLevel.Trace,
        _ => ForecourtLogLevel.Info,
    };

    /// <summary>
    /// Matches ElApp.AuthService.Web's AppSettings.LogLevelsToPersist / ElApp.MainExternal.Service's
    /// equivalent exactly - same "Logging:LogLevelsToPersist" config key, same comma-separated
    /// LogEventLevel-name format, same "none" (persist nothing) and unset (persist Warning and above)
    /// handling - so this app's appsettings.json reads the same way as every other service's.
    /// </summary>
    public static List<LogEventLevel> ParseLogLevelsToPersist(IConfiguration configuration)
    {
        var value = configuration["Logging:LogLevelsToPersist"];

        if (string.IsNullOrWhiteSpace(value))
        {
            return [LogEventLevel.Fatal, LogEventLevel.Error, LogEventLevel.Warning];
        }

        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return value
            .Replace(" ", string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(level => Enum.TryParse(level, ignoreCase: true, out LogEventLevel parsed) ? parsed : (LogEventLevel?)null)
            .Where(level => level.HasValue)
            .Select(level => level!.Value)
            .ToList();
    }
}

/// <summary>
/// Mirrors Fc.Common.Logger.FcCommonSink exactly (see decompiled Fc.Common.Logger.dll) - a thin
/// ILogEventSink that hands each event to a callback fire-and-forget, since Serilog's Emit is synchronous
/// and must never block on a network call. Not reusing Fc.Common.Logger's own copy directly: that package
/// pulls in RestSharp/Fc.Common.Elements/token-acquisition machinery this app doesn't otherwise need,
/// just to get one 10-line class - and its paired FcLogger.LogSeriLog always posts directly with a fixed
/// service identity, which isn't the routing this app needs (see ForecourtSerilogLogging's class remarks).
/// </summary>
public sealed class ForecourtSerilogSink : ILogEventSink
{
    private readonly Func<LogEvent, Task> _handleLogEvent;

    public ForecourtSerilogSink(Func<LogEvent, Task> handleLogEvent)
    {
        _handleLogEvent = handleLogEvent ?? throw new ArgumentNullException(nameof(handleLogEvent));
    }

    public void Emit(LogEvent logEvent) => _ = _handleLogEvent(logEvent);
}
