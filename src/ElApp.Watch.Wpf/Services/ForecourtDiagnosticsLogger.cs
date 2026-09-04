using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Net.Http.Json;

namespace ElApp.Watch.Wpf.Services;

public sealed class ForecourtDiagnosticsLogger : IForecourtDiagnosticsLogger
{
    private const string LogType = "ForecourtWatch";
    private const string ForecourtClientIdPrefix = "el-";

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
        // with the customer's elid, extracted from the forecourt client_id (whatever's configured:
        // appsettings' ForecourtAuth:SeedClientId while set, otherwise Windows Credential Manager - see
        // ConfigOverridingCredentialStore), regardless of whether that credential currently authenticates
        // - a wrong/expired secret is exactly the case where the public channel is used and station
        // attribution matters most. Extraction happens because ElApp.Logger.Service's UserId column is
        // a Guid? and LogService.LogMessage silently drops anything Guid.TryParse can't parse: a real
        // client_id is "el-{elid}", not a bare guid, so sending it unmodified would never persist even
        // though it's genuinely GUID-shaped once the prefix is removed.
        var userId = _credentialStore.TryGet()?.ClientId;
        var model = new ForecourtPublicLogModel
        {
            ApplicationIdentifier = _options.ApplicationIdentifier,
            LogLevel = level,
            Title = title,
            Message = message,
            // Host (below) doesn't reach the log DB yet - ElApp.MainExternal.Service's public-channel
            // proxy binds to a published NuGet package that hasn't picked up the new Host field. Until
            // that's redeployed, put the IP in MoreInfo too, which is already known to reach the DB.
            MoreInfo = $"[UserId: {userId}] [IP: {LocalNetworkInfo.LocalIpAddress}] " + moreInfo,
            Type = LogType,
            UserId = TryExtractElid(userId),
            Host = LocalNetworkInfo.LocalIpAddress,
        };
        return PostSafelyAsync(
            () => _anonymousHttpClient.PostAsJsonAsync(_options.PublicLogEndpoint, model, cancellationToken),
            "public",
            cancellationToken);
    }

    /// <summary>
    /// Forecourt client_ids are "el-{elid}" (ElApp.AuthService.Web's IForecourtClientProvisioningService),
    /// not a bare guid - but ElApp.Logger.Service's UserId column is a Guid? and LogService.LogMessage
    /// silently drops anything Guid.TryParse can't parse (no error, just null). Sending the client_id
    /// as-is would therefore never persist, even for a genuine one - strip the prefix first so the
    /// actual elid (what UserId is meant to hold) reaches the database. A configured value that isn't
    /// "el-{guid}"-shaped at all (e.g. a placeholder test value) correctly yields null here too - it was
    /// never representable in that Guid? column regardless of this extraction.
    /// </summary>
    private static string? TryExtractElid(string? clientId)
    {
        if (clientId is null || !clientId.StartsWith(ForecourtClientIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var candidate = clientId[ForecourtClientIdPrefix.Length..];
        return Guid.TryParse(candidate, out var elid) ? elid.ToString() : null;
    }

    private Task<bool> PostToPrivateEndpointAsync(ForecourtLogLevel level, string title, string message, string? moreInfo, CancellationToken cancellationToken)
    {
        var model = new ForecourtPrivateLogModel
        {
            ApplicationIdentifier = _options.ApplicationIdentifier,
            LogLevel = level,
            Title = title,
            Message = message,
            MoreInfo = $"[IP: {LocalNetworkInfo.LocalIpAddress}] " + moreInfo,
            Type = LogType,
            Source = _options.ApplicationIdentifier,
            Host = LocalNetworkInfo.LocalIpAddress,
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
