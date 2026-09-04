using Microsoft.Extensions.Options;

namespace ElApp.Watch.Forecourt;

/// <summary>
/// Decorator over the real <see cref="IForecourtCredentialStore"/>: when appsettings.json's
/// ForecourtAuth:ClientId/ClientSecret are both set, every read returns them directly - Windows
/// Credential Manager is not consulted at all in that case, so editing appsettings.json and relaunching
/// always takes effect immediately with nothing else to reason about. Only when both are blank does a
/// read fall through to the real store. <see cref="Save"/> always writes through to the real store
/// regardless, so Windows Credential Manager stays usable as a fallback for a station that hasn't been
/// given a config-level credential.
/// </summary>
public sealed class ConfigOverridingCredentialStore : IForecourtCredentialStore
{
    private readonly IForecourtCredentialStore _inner;
    private readonly ForecourtAuthOptions _options;

    public ConfigOverridingCredentialStore(IForecourtCredentialStore inner, IOptions<ForecourtAuthOptions> options)
    {
        _inner = inner;
        _options = options.Value;
    }

    public ForecourtCredential? TryGet()
    {
        if (!string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret))
        {
            return new ForecourtCredential(_options.ClientId, _options.ClientSecret);
        }

        return _inner.TryGet();
    }

    public void Save(ForecourtCredential credential) => _inner.Save(credential);
}
