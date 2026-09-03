using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Options;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// TEMPORARY, testing-only decorator over the real <see cref="IForecourtCredentialStore"/>: when
/// appsettings.json's ForecourtAuth:SeedClientId/SeedClientSecret are both set, every read returns them
/// directly - Windows Credential Manager is not consulted at all in that case, so editing appsettings.json
/// and relaunching always takes effect immediately with nothing else to reason about (no seed step, no
/// "is the store empty" question). Only when both are blank does a read fall through to the real store.
/// <see cref="Save"/> always writes through to the real store regardless, so the real
/// provisioning/setup flow this stands in for keeps working underneath.
/// A real forecourt device must never have its client_secret sit in a config file at all - see
/// <see cref="WindowsCredentialManagerStore"/> - so this type should be removed, and callers should take
/// the wrapped store directly, once a real provisioning/setup flow exists.
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
        if (!string.IsNullOrWhiteSpace(_options.SeedClientId) && !string.IsNullOrWhiteSpace(_options.SeedClientSecret))
        {
            return new ForecourtCredential(_options.SeedClientId, _options.SeedClientSecret);
        }

        return _inner.TryGet();
    }

    public void Save(ForecourtCredential credential) => _inner.Save(credential);
}
