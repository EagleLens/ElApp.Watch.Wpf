namespace ElApp.Watch.Forecourt;

/// <summary>
/// ElApp.MainExternal.Service's fixed endpoint paths, hardcoded here and resolved against the
/// environment-specific <see cref="MainExternalApiOptions.BaseUrl"/> - see that class's remarks. Keeping
/// the paths in code instead of appsettings.json means an environment's config only ever needs to set the
/// one base URL, rather than repeating it once per endpoint.
/// </summary>
public static class MainExternalApiEndpoints
{
    private const string PublicLogPath = "22bec587/9d8aacd3526b444890b42c2e04ebc416";
    private const string PrivateLogPath = "cdcd9ec5/d189a9bc736249298f4f8e837650a8c1";
    private const string ProcessImagePath = "041fb6f0/a59a23fbf7564cefa4195393e44f5d34";

    /// <summary>ElApp.MainExternal.Service's public (AllowAnonymous) log-message endpoint. See
    /// LoggerPublicLoggerController.</summary>
    public static string PublicLog(string baseUrl) => Combine(baseUrl, PublicLogPath);

    /// <summary>ElApp.MainExternal.Service's private (bearer-token-secured) log-message endpoint. See
    /// LoggerPrivateLoggerController.</summary>
    public static string PrivateLog(string baseUrl) => Combine(baseUrl, PrivateLogPath);

    /// <summary>ElApp.MainExternal.Service's MainPrivateImageProcessingController.ProcessImage
    /// endpoint.</summary>
    public static string ProcessImage(string baseUrl) => Combine(baseUrl, ProcessImagePath);

    private static string Combine(string baseUrl, string path) => $"{baseUrl.TrimEnd('/')}/{path}";
}
