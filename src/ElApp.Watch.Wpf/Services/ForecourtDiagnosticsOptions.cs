namespace ElApp.Watch.Wpf.Services;

/// <summary>Bound from the "ForecourtDiagnostics" section of appsettings.json.</summary>
public sealed class ForecourtDiagnosticsOptions
{
    public const string SectionName = "ForecourtDiagnostics";

    /// <summary>Identifies this app to the logging services, matching the convention other EagleLens
    /// apps use for their own "Application:Identifier" config value.</summary>
    public string ApplicationIdentifier { get; init; } = "el-watch-wpf";

    /// <summary>
    /// ElApp.MainExternal.Service's public (AllowAnonymous) log-message endpoint - used when the app
    /// cannot authenticate at all, so the server still finds out something is wrong even without a
    /// bearer token. See LoggerPublicLoggerController.
    /// </summary>
    public required string PublicLogEndpoint { get; init; }

    /// <summary>
    /// ElApp.MainExternal.Service's private (bearer-token-secured) log-message endpoint - used for
    /// problems that occur after the app has already authenticated successfully. See
    /// LoggerPrivateLoggerController.
    /// </summary>
    public required string PrivateLogEndpoint { get; init; }
}
