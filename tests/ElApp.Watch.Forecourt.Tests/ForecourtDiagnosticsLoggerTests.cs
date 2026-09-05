using System.Net;
using System.Text;
using System.Text.Json;
using ElApp.Watch.Forecourt;
using Microsoft.Extensions.Options;
using Xunit;

namespace ElApp.Watch.Forecourt.Tests;

/// <summary>
/// Verifies IForecourtDiagnosticsLogger's routing decision (public vs private, decided internally by
/// whether a forecourt access token can currently be obtained - never by the caller), its public-endpoint
/// fallback when private delivery fails, and that it never throws even when every channel fails. Uses a
/// stub HttpMessageHandler, a fake IForecourtApiClient, and a fake IForecourtTokenClient instead of a
/// mocking library (none referenced in this project).
/// </summary>
public class ForecourtDiagnosticsLoggerTests
{
    private static readonly ForecourtDiagnosticsOptions Options = new()
    {
        ApplicationIdentifier = "el-watch-wpf-test",
        PublicLogEndpoint = "https://example.test/public-log",
        PrivateLogEndpoint = "https://example.test/private-log",
    };

    [Fact]
    public async Task LogAsync_when_no_token_is_obtainable_posts_to_the_public_endpoint_with_no_auth_header()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var apiClient = new ThrowingApiClient();
        var tokenClient = new FakeTokenClient(token: null);
        var sut = CreateSut(handler, apiClient, tokenClient);

        await sut.LogAsync(ForecourtLogLevel.Info, "title", "message", "more info");

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/public-log", handler.Requests[0].Uri);
        Assert.Null(handler.Requests[0].AuthHeader);
        // JsonContent.Create defaults to camelCase - fine, ASP.NET Core's server-side binding is
        // case-insensitive either way.
        using var document = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal("title", document.RootElement.GetProperty("title").GetString());
        Assert.Equal("el-watch-wpf-test", document.RootElement.GetProperty("applicationIdentifier").GetString());
        Assert.Equal((int)ForecourtLogLevel.Info, document.RootElement.GetProperty("logLevel").GetInt32());
        Assert.False(apiClient.WasCalled); // the authenticated client must never be used when no token exists
    }

    [Fact]
    public async Task LogAsync_public_entry_carries_the_extracted_elid_as_UserId()
    {
        // No bearer token on the public path, so UserId is the only way the server can tell which
        // station sent this entry - populate it from the configured client_id even though this specific
        // call couldn't authenticate with it (e.g. a wrong/expired secret - exactly when public-channel
        // attribution matters most). ElApp.Logger.Service's UserId column is a Guid? and silently drops
        // anything that isn't - the "el-" prefix must be stripped so the bare elid actually persists.
        var elid = Guid.NewGuid();
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var sut = CreateSut(handler, new ThrowingApiClient(), new FakeTokenClient(token: null), credentialClientId: $"el-{elid}");

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        using var document = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(elid.ToString(), document.RootElement.GetProperty("userId").GetString());
    }

    [Theory]
    [InlineData(null)] // no credential stored at all
    [InlineData("534534543")] // not "el-"-prefixed at all - a placeholder that was never guid-shaped
    [InlineData("el-not-a-guid")] // "el-"-prefixed but the remainder isn't a valid guid
    public async Task LogAsync_public_entry_UserId_is_null_when_the_stored_client_id_has_no_extractable_elid(string? credentialClientId)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var sut = CreateSut(handler, new ThrowingApiClient(), new FakeTokenClient(token: null), credentialClientId);

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        using var document = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task LogAsync_when_a_token_is_obtained_posts_to_the_private_endpoint_via_the_authenticated_client()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var apiClient = new RecordingApiClient();
        var tokenClient = new FakeTokenClient(token: "token-1");
        var sut = CreateSut(handler, apiClient, tokenClient);

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        Assert.Empty(handler.Requests); // must not go through the anonymous client
        Assert.Equal("https://example.test/private-log", apiClient.LastRequestUri);
    }

    [Fact]
    public async Task LogAsync_public_entry_carries_the_station_ip_as_Host()
    {
        // Identifies which physical station sent the entry on the local network - independent of
        // ApplicationIdentifier/UserId, which identify the app/customer, not the machine.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var sut = CreateSut(handler, new ThrowingApiClient(), new FakeTokenClient(token: null));

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        using var document = JsonDocument.Parse(handler.Requests[0].Body!);
        var hostProperty = document.RootElement.GetProperty("host");
        var actual = hostProperty.ValueKind == JsonValueKind.Null ? null : hostProperty.GetString();
        Assert.Equal(LocalNetworkInfo.LocalIpAddress, actual);
    }

    [Fact]
    public async Task LogAsync_private_entry_carries_the_station_ip_as_Host()
    {
        var apiClient = new RecordingApiClient();
        var sut = CreateSut(new StubHttpMessageHandler(HttpStatusCode.OK), apiClient, new FakeTokenClient(token: "token-1"));

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        var model = Assert.IsType<ForecourtPrivateLogModel>(apiClient.LastBody);
        Assert.Equal(LocalNetworkInfo.LocalIpAddress, model.Host);
    }

    [Fact]
    public async Task LogAsync_falls_back_to_the_public_endpoint_when_private_delivery_fails()
    {
        // A token was obtained, but the private channel itself can't deliver - plausibly because
        // whatever's wrong also breaks this call. The entry must still reach the server somehow.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK);
        var tokenClient = new FakeTokenClient(token: "token-1");
        var sut = CreateSut(handler, new ThrowingApiClient(), tokenClient);

        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");

        Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/public-log", handler.Requests[0].Uri);
        Assert.Null(handler.Requests[0].AuthHeader);
    }

    [Fact]
    public async Task LogAsync_does_not_throw_when_every_channel_fails()
    {
        var sut = CreateSut(new ThrowingHttpMessageHandler(), new ThrowingApiClient(), new FakeTokenClient(token: "token-1"));

        // Reporting a problem must never itself throw, even if every reporting channel is broken.
        await sut.LogAsync(ForecourtLogLevel.Error, "title", "message");
    }

    private static ForecourtDiagnosticsLogger CreateSut(
        HttpMessageHandler anonymousHandler, IForecourtApiClient apiClient, IForecourtTokenClient tokenClient, string? credentialClientId = null) =>
        new(
            new HttpClient(anonymousHandler),
            apiClient,
            tokenClient,
            new FakeCredentialStore(credentialClientId),
            Microsoft.Extensions.Options.Options.Create(Options));

    private sealed record CapturedRequest(string Uri, string? AuthHeader, string? Body);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public List<CapturedRequest> Requests { get; } = new();

        public StubHttpMessageHandler(HttpStatusCode status)
        {
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return new HttpResponseMessage(_status) { Content = new StringContent(string.Empty, Encoding.UTF8) };
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }

    /// <summary>Returns a fixed token, or throws (simulating "no credential"/"token endpoint unreachable") if <c>token</c> is null.</summary>
    private sealed class FakeTokenClient : IForecourtTokenClient
    {
        private readonly string? _token;

        public FakeTokenClient(string? token)
        {
            _token = token;
        }

        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            _token is null
                ? throw new InvalidOperationException("No forecourt credential has been provisioned on this station yet.")
                : Task.FromResult(_token);
    }

    /// <summary>Fake IForecourtApiClient that must never be called when no token is obtainable.</summary>
    private sealed class ThrowingApiClient : IForecourtApiClient
    {
        public bool WasCalled { get; private set; }

        public Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("should not be called");
        }

        public Task<HttpResponseMessage> PostAsJsonAsync<TBody>(string requestUri, TBody body, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("simulated failure");
        }

        public Task<HttpResponseMessage> PostFileAsync(string requestUri, byte[] fileContent, string fileName, string contentType, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("should not be called");
        }
    }

    private sealed class FakeCredentialStore : IForecourtCredentialStore
    {
        private readonly string? _clientId;

        public FakeCredentialStore(string? clientId)
        {
            _clientId = clientId;
        }

        public ForecourtCredential? TryGet() => _clientId is null ? null : new ForecourtCredential(_clientId, "unused-secret");

        public void Save(ForecourtCredential credential) => throw new NotSupportedException();
    }

    private sealed class RecordingApiClient : IForecourtApiClient
    {
        public string? LastRequestUri { get; private set; }
        public object? LastBody { get; private set; }

        public Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<HttpResponseMessage> PostAsJsonAsync<TBody>(string requestUri, TBody body, CancellationToken cancellationToken = default)
        {
            LastRequestUri = requestUri;
            LastBody = body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty, Encoding.UTF8) });
        }

        public Task<HttpResponseMessage> PostFileAsync(string requestUri, byte[] fileContent, string fileName, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
