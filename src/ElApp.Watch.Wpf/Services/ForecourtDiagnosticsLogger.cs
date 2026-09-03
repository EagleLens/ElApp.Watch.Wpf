using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Http.Json;

namespace ElApp.Watch.Wpf.Services;

public sealed class ForecourtDiagnosticsLogger : IForecourtDiagnosticsLogger
{
    private const string LogType = "ForecourtWatch";

    private readonly HttpClient _anonymousHttpClient;
    private readonly IForecourtApiClient _authenticatedApiClient;
    private readonly IForecourtTokenClient _tokenClient;
    private readonly IForecourtCredentialStore _credentialStore;
    private readonly ForecourtDiagnosticsOptions _options;
    private readonly ILogger<ForecourtDiagnosticsLogger> _logger;

    public ForecourtDiagnosticsLogger(
        HttpClient anonymousHttpClient,
        IForecourtApiClient authenticatedApiClient,
        IForecourtTokenClient tokenClient,
        IForecourtCredentialStore credentialStore,
        IOptions<ForecourtDiagnosticsOptions> options,
        ILogger<ForecourtDiagnosticsLogger> logger)
    {
        _anonymousHttpClient = anonymousHttpClient;
        _authenticatedApiClient = authenticatedApiClient;
        _tokenClient = tokenClient;
        _credentialStore = credentialStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task LogAsync(ForecourtLogLevel level, string title, string message, string? moreInfo = null, CancellationToken cancellationToken = default)
    {
        var authenticated = await TryAuthenticateAsync(cancellationToken);

        if (authenticated && await PostToPrivateEndpointAsync(level, title, message, moreInfo, cancellationToken))
        {
            return;
        }

        if (authenticated)
        {
            // The private (authenticated) channel itself failed to deliver - plausibly because whatever
            // is wrong also breaks this call (e.g. a token the server no longer accepts). Fall back to
            // the public endpoint so the entry still reaches the server rather than being lost.
            _logger.LogWarning("Private log delivery failed; falling back to the public endpoint.");
        }

        await PostToPublicEndpointAsync(level, title, message, moreInfo, cancellationToken);
    }

    private async Task<bool> TryAuthenticateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _tokenClient.GetAccessTokenAsync(forceRefresh: false, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not obtain a forecourt access token; reporting via the public log endpoint.");
            return false;
        }
    }

    private Task<bool> PostToPublicEndpointAsync(ForecourtLogLevel level, string title, string message, string? moreInfo, CancellationToken cancellationToken)
    {
        // There's no bearer token on this path (that's why we're using the public endpoint), so
        // UserId is the only way the server can attribute this entry to a station at all - populate it
        // directly from whatever client_id is configured (appsettings' ForecourtAuth:SeedClientId while
        // set, otherwise Windows Credential Manager - see ConfigOverridingCredentialStore). Note
        // ElApp.Logger.Service's UserId column is a Guid? (LogService.LogMessage silently drops
        // anything Guid.TryParse can't parse) - a non-guid client_id, or "el-{guid}"'s "el-" prefix,
        // will therefore still come through as null in the log DB even though it was sent. That's a
        // server-side (ElApp.Logger.Service) constraint, not something this app can work around.
        var model = new ForecourtPublicLogModel
        {
            ApplicationIdentifier = _options.ApplicationIdentifier,
            LogLevel = level,
            Title = title,
            Message = message,
            MoreInfo = moreInfo,
            Type = LogType,
            UserId = _credentialStore.TryGet()?.ClientId,
        };
        return PostSafelyAsync(
            () => _anonymousHttpClient.PostAsJsonAsync(_options.PublicLogEndpoint, model, cancellationToken),
            "public",
            cancellationToken);
    }

    private Task<bool> PostToPrivateEndpointAsync(ForecourtLogLevel level, string title, string message, string? moreInfo, CancellationToken cancellationToken)
    {
        var model = new ForecourtPrivateLogModel
        {
            ApplicationIdentifier = _options.ApplicationIdentifier,
            LogLevel = level,
            Title = title,
            Message = message,
            MoreInfo = moreInfo,
            Type = LogType,
            Source = _options.ApplicationIdentifier,
        };
        return PostSafelyAsync(
            () => _authenticatedApiClient.PostAsJsonAsync(_options.PrivateLogEndpoint, model, cancellationToken),
            "private",
            cancellationToken);
    }

    /// <returns><c>true</c> if the request was sent and got a success status; <c>false</c> otherwise.
    /// Never throws - this exists to report OTHER failures, so it must fail safe itself.</returns>
    private async Task<bool> PostSafelyAsync(Func<Task<HttpResponseMessage>> send, string endpointKind, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            _logger.LogWarning(
                "Forecourt diagnostics log ({EndpointKind}) rejected by server: {StatusCode}",
                endpointKind,
                response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to submit forecourt diagnostics log ({EndpointKind}).", endpointKind);
            return false;
        }
    }
}
