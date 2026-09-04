namespace ElApp.Watch.Forecourt;

/// <summary>
/// Reports this station's events (startup, errors, and anything else worth telling the platform about)
/// to ElApp.MainExternal.Service's logging endpoints, so the admin knows what's going on with an
/// unattended device even when nobody is watching it locally.
///
/// Which endpoint carries a given entry is an implementation decision, not the caller's: this type
/// attempts to obtain the station's forecourt bearer token before every call, and routes accordingly -
/// no token obtainable -> the public, unauthenticated endpoint (LoggerPublicLoggerController), since
/// there is nothing to authenticate a private call with; a token obtained -> the private, bearer-secured
/// endpoint (LoggerPrivateLoggerController), so the entry is attributable to this station's customer. If
/// delivery to the private endpoint itself fails, this falls back to the public endpoint rather than
/// losing the entry.
///
/// Every call is fail-safe: a failure to deliver a log entry is caught and logged locally via
/// <c>ILogger</c>, never thrown - logging must not become a new source of failures.
/// </summary>
public interface IForecourtDiagnosticsLogger
{
    /// <summary>
    /// Logs one event. <paramref name="title"/> is a short summary; <paramref name="message"/> and the
    /// optional <paramref name="moreInfo"/> carry the detail (e.g. an exception's message and its full
    /// <c>ToString()</c>, respectively).
    /// </summary>
    Task LogAsync(ForecourtLogLevel level, string title, string message, string? moreInfo = null, CancellationToken cancellationToken = default);
}
