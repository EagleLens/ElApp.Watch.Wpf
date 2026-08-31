namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// The forecourt device's OpenIddict client_credentials (client_id/client_secret) pair, as issued by
/// ElApp.AuthService.Web's admin create-customer action.
/// </summary>
public sealed record ForecourtCredential(string ClientId, string ClientSecret);

/// <summary>
/// Stores/retrieves this station's forecourt client_id/client_secret in Windows Credential Manager
/// (DPAPI-backed OS-level secure storage), per <c>ARCHITECTURE.md</c> §6 and the
/// <c>forecourt-client-credentials-auth</c> openspec change's design.md Decision 8. Written once at setup
/// time; the station reads it back on every startup for the lifetime of the device (years, unattended) -
/// there is no periodic re-entry.
/// </summary>
public interface IForecourtCredentialStore
{
    /// <summary>
    /// Returns the stored credential, or <c>null</c> if none has been written yet (first-run/unprovisioned
    /// state - the station has not been set up).
    /// </summary>
    ForecourtCredential? TryGet();

    /// <summary>Writes (or overwrites) the credential. Called once, during station setup.</summary>
    void Save(ForecourtCredential credential);
}
