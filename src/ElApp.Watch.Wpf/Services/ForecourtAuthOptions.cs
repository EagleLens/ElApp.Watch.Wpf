using ElApp.Watch.Wpf.Services.Interface;

namespace ElApp.Watch.Wpf.Services;

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
    /// TEMPORARY, testing-only: when both this and <see cref="SeedClientSecret"/> are set, every
    /// credential read returns them directly - see <see cref="ConfigOverridingCredentialStore"/> - and
    /// Windows Credential Manager is not consulted at all. Leave both blank to use whatever is actually
    /// stored in Windows Credential Manager (the real, production path). A real forecourt device must
    /// never have its client_secret sit in a config file at all - that's exactly what Windows Credential
    /// Manager exists to avoid (see <see cref="WindowsCredentialManagerStore"/>) - so this exists only
    /// until a real provisioning/setup flow (CLI seeding, an installer step, etc.) replaces it.
    /// </summary>
    public string? SeedClientId { get; init; }

    public string? SeedClientSecret { get; init; }

    /// <summary>
    /// TEMPORARY, testing-only: if set, App.xaml.cs makes one authenticated GET to this URL on startup via
    /// <see cref="IForecourtApiClient"/> and shows the result, to prove the client_credentials -> bearer
    /// token -> API call path works end to end. Not part of any real verify-flow integration (that has its
    /// own endpoint contract, request/response shape, and error handling still to be designed) - remove
    /// this smoke-test call once that exists.
    /// </summary>
    public string? StartupTestRequestUrl { get; init; }
}
