namespace ElApp.Watch.Forecourt;

/// <summary>Bound from the "ForecourtAuth" section of appsettings.json.</summary>
public sealed class ForecourtAuthOptions
{
    public const string SectionName = "ForecourtAuth";

    /// <summary>ElApp.AuthService.Web's OpenIddict token endpoint (e.g. ".../connect/token").</summary>
    public required string TokenEndpoint { get; init; }

    /// <summary>
    /// How long before the cached access token's actual expiry to proactively fetch a new one, so a
    /// verify-flow call never race-fails against a token expiring mid-request.
    /// </summary>
    public TimeSpan RefreshMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// This station's forecourt client_id, provisioned by an admin via
    /// <c>ElApp.AuthService.Web</c>'s <c>AdminCustomerController</c> and configured here directly. When
    /// both this and <see cref="ClientSecret"/> are set, every credential read returns them directly -
    /// see <see cref="ConfigOverridingCredentialStore"/> - without consulting Windows Credential Manager.
    /// Leave both blank to fall back to whatever is stored in Windows Credential Manager instead (see
    /// <see cref="WindowsCredentialManagerStore"/>).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>This station's forecourt client_secret, paired with <see cref="ClientId"/>.</summary>
    public string? ClientSecret { get; init; }
}
