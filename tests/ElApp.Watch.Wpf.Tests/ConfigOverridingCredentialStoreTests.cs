using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using Microsoft.Extensions.Options;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// See openspec change forecourt-client-credentials-auth. Verifies appsettings.json's ForecourtAuth:
/// SeedClientId/SeedClientSecret take over reads entirely whenever both are set - the inner (real) store
/// is not consulted - and that reads fall through to the inner store when either is blank.
/// </summary>
public class ConfigOverridingCredentialStoreTests
{
    [Fact]
    public void TryGet_returns_the_configured_values_directly_when_both_are_set()
    {
        var inner = new FakeInnerStore(new ForecourtCredential("el-inner", "inner-secret"));
        var sut = CreateSut(inner, seedClientId: "el-config", seedClientSecret: "config-secret");

        var result = sut.TryGet();

        Assert.Equal("el-config", result!.ClientId);
        Assert.Equal("config-secret", result.ClientSecret);
        Assert.False(inner.TryGetWasCalled); // must not consult the real store at all
    }

    [Theory]
    [InlineData(null, "config-secret")]
    [InlineData("el-config", null)]
    [InlineData("", "config-secret")]
    [InlineData("el-config", "")]
    public void TryGet_falls_through_to_the_inner_store_when_either_config_value_is_blank(string? seedClientId, string? seedClientSecret)
    {
        var inner = new FakeInnerStore(new ForecourtCredential("el-inner", "inner-secret"));
        var sut = CreateSut(inner, seedClientId, seedClientSecret);

        var result = sut.TryGet();

        Assert.Equal("el-inner", result!.ClientId);
        Assert.True(inner.TryGetWasCalled);
    }

    [Fact]
    public void Save_always_writes_through_to_the_inner_store_even_when_config_values_are_set()
    {
        var inner = new FakeInnerStore(existing: null);
        var sut = CreateSut(inner, seedClientId: "el-config", seedClientSecret: "config-secret");

        sut.Save(new ForecourtCredential("el-real", "real-secret"));

        Assert.Equal("el-real", inner.LastSaved?.ClientId);
    }

    private static ConfigOverridingCredentialStore CreateSut(IForecourtCredentialStore inner, string? seedClientId, string? seedClientSecret)
    {
        var options = new ForecourtAuthOptions
        {
            TokenEndpoint = "https://example.test/connect/token",
            SeedClientId = seedClientId,
            SeedClientSecret = seedClientSecret,
        };
        return new ConfigOverridingCredentialStore(inner, Options.Create(options));
    }

    private sealed class FakeInnerStore : IForecourtCredentialStore
    {
        private readonly ForecourtCredential? _existing;

        public FakeInnerStore(ForecourtCredential? existing)
        {
            _existing = existing;
        }

        public bool TryGetWasCalled { get; private set; }
        public ForecourtCredential? LastSaved { get; private set; }

        public ForecourtCredential? TryGet()
        {
            TryGetWasCalled = true;
            return _existing;
        }

        public void Save(ForecourtCredential credential) => LastSaved = credential;
    }
}
