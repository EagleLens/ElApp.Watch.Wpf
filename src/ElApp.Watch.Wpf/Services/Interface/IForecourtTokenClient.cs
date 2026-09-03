namespace ElApp.Watch.Wpf.Services.Interface;

/// <summary>
/// Obtains and caches a short-lived access token from ElApp.AuthService.Web's <c>/connect/token</c>
/// endpoint using the station's stored forecourt client_credentials, transparently re-fetching shortly
/// before expiry. No refresh token is used or needed for this grant type - the durable artifact is the
/// client_secret in <see cref="IForecourtCredentialStore"/>, not any token; a new token is obtained the
/// same way every time, indefinitely, with no manual renewal ever required. See the
/// <c>forecourt-client-credentials-auth</c> openspec change's design.md Decision 8.
/// </summary>
public interface IForecourtTokenClient
{
    /// <summary>
    /// Returns a currently-valid access token, fetching a new one if none is cached or the cached one is
    /// within its refresh margin of expiring.
    /// </summary>
    /// <param name="forceRefresh">
    /// Bypasses the cache and fetches a fresh token even if the cached one has not reached its refresh
    /// margin yet - for a caller that received a 401 from a resource server using the cached token (e.g.
    /// server-side revocation) and wants a token it didn't already try. No actual call site does this yet
    /// (see the forecourt-client-credentials-auth change's task 5.5 - this client isn't wired into a
    /// verify-API call flow in this change), so this parameter exists for that future integration to use
    /// rather than being exercised by any caller today.
    /// </param>
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
