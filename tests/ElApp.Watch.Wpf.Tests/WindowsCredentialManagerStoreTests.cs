using ElApp.Watch.Wpf.Services;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// Exercises the real Windows Credential Manager (no fake/mock - there is no first-party managed wrapper
/// to substitute) via <see cref="WindowsCredentialManagerStore"/>. See openspec change
/// <c>forecourt-client-credentials-auth</c>, tasks 5.1-5.2. Windows-only, per the project file's comment.
/// Each test uses a unique target name so it starts from - and cleans up to - a guaranteed-absent state,
/// independent of any other test or previous run.
/// </summary>
public class WindowsCredentialManagerStoreTests : IDisposable
{
    private readonly WindowsCredentialManagerStore _sut = new($"EagleLens:ForecourtClient:Test:{Guid.NewGuid()}");

    [Fact]
    public void TryGet_returns_null_before_any_credential_has_been_written()
    {
        Assert.Null(_sut.TryGet());
    }

    [Fact]
    public void Save_then_TryGet_round_trips_the_client_id_and_secret()
    {
        var credential = new ForecourtCredential($"el-{Guid.NewGuid()}", $"secret-{Guid.NewGuid():N}");

        _sut.Save(credential);
        var retrieved = _sut.TryGet();

        Assert.NotNull(retrieved);
        Assert.Equal(credential.ClientId, retrieved!.ClientId);
        Assert.Equal(credential.ClientSecret, retrieved.ClientSecret);
    }

    [Fact]
    public void Save_overwrites_a_previously_stored_credential()
    {
        _sut.Save(new ForecourtCredential("el-old", "old-secret"));

        var replacement = new ForecourtCredential("el-new", "new-secret");
        _sut.Save(replacement);
        var retrieved = _sut.TryGet();

        Assert.NotNull(retrieved);
        Assert.Equal(replacement.ClientId, retrieved!.ClientId);
        Assert.Equal(replacement.ClientSecret, retrieved.ClientSecret);
    }

    public void Dispose()
    {
        _sut.Delete();
    }
}
