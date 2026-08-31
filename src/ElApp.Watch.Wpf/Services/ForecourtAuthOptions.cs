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
}
