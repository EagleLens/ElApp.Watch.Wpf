using System.Net;
using System.Text;
using ElApp.Watch.Wpf.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// See openspec change <c>forecourt-client-credentials-auth</c>, tasks 5.3-5.4. Uses a stub
/// <see cref="HttpMessageHandler"/> and a manually-advanceable <see cref="TimeProvider"/> instead of a
/// mocking library (none referenced in this project) or a real network call.
/// </summary>
public class ForecourtTokenClientTests
{
    private static readonly ForecourtAuthOptions Options = new()
    {
        TokenEndpoint = "https://auth.example.test/connect/token",
        RefreshMargin = TimeSpan.FromMinutes(2),
    };

    [Fact]
    public async Task GetAccessTokenAsync_fetches_a_token_using_the_stored_credential()
    {
        var credentialStore = new FakeCredentialStore(new ForecourtCredential("el-abc", "shh"));
        var handler = new StubHttpMessageHandler(TokenResponseJson("token-1", expiresInSeconds: 3600));
        var client = CreateSut(credentialStore, handler, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

        var token = await client.GetAccessTokenAsync();

        Assert.Equal("token-1", token);
        Assert.Single(handler.RequestBodies);
        var body = handler.RequestBodies[0];
        Assert.Contains("grant_type=client_credentials", body);
        Assert.Contains("client_id=el-abc", body);
        Assert.Contains("client_secret=shh", body);
    }

    [Fact]
    public async Task GetAccessTokenAsync_returns_the_cached_token_when_well_within_its_lifetime()
    {
        var credentialStore = new FakeCredentialStore(new ForecourtCredential("el-abc", "shh"));
        var handler = new StubHttpMessageHandler(TokenResponseJson("token-1", expiresInSeconds: 3600));
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var client = CreateSut(credentialStore, handler, timeProvider);

        var first = await client.GetAccessTokenAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(10));
        var second = await client.GetAccessTokenAsync();

        Assert.Equal("token-1", first);
        Assert.Equal("token-1", second);
        Assert.Single(handler.RequestBodies); // no second HTTP call
    }

    [Fact]
    public async Task GetAccessTokenAsync_transparently_refetches_shortly_before_expiry()
    {
        var credentialStore = new FakeCredentialStore(new ForecourtCredential("el-abc", "shh"));
        var handler = new StubHttpMessageHandler(TokenResponseJson("token-1", expiresInSeconds: 3600));
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var client = CreateSut(credentialStore, handler, timeProvider);

        var first = await client.GetAccessTokenAsync();

        // 3600s lifetime, 2-minute refresh margin: advancing 59 minutes puts us inside the margin.
        timeProvider.Advance(TimeSpan.FromMinutes(59));
        handler.NextResponseBody = TokenResponseJson("token-2", expiresInSeconds: 3600);
        var second = await client.GetAccessTokenAsync();

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", second);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_forceRefresh_bypasses_a_still_valid_cached_token()
    {
        var credentialStore = new FakeCredentialStore(new ForecourtCredential("el-abc", "shh"));
        var handler = new StubHttpMessageHandler(TokenResponseJson("token-1", expiresInSeconds: 3600));
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var client = CreateSut(credentialStore, handler, timeProvider);

        var first = await client.GetAccessTokenAsync();
        // Well within the cached token's lifetime - a plain call would return the cached value.
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        handler.NextResponseBody = TokenResponseJson("token-2", expiresInSeconds: 3600);
        var forced = await client.GetAccessTokenAsync(forceRefresh: true);

        Assert.Equal("token-1", first);
        Assert.Equal("token-2", forced);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task GetAccessTokenAsync_never_sends_or_expects_a_refresh_token()
    {
        var credentialStore = new FakeCredentialStore(new ForecourtCredential("el-abc", "shh"));
        var handler = new StubHttpMessageHandler(TokenResponseJson("token-1", expiresInSeconds: 3600));
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var client = CreateSut(credentialStore, handler, timeProvider);

        await client.GetAccessTokenAsync();
        timeProvider.Advance(TimeSpan.FromDays(400)); // years later - still no refresh token involved
        handler.NextResponseBody = TokenResponseJson("token-2", expiresInSeconds: 3600);
        var refetched = await client.GetAccessTokenAsync();

        Assert.Equal("token-2", refetched);
        foreach (var body in handler.RequestBodies)
        {
            Assert.DoesNotContain("refresh_token", body);
            Assert.Contains("grant_type=client_credentials", body);
        }
    }

    private static string TokenResponseJson(string accessToken, int expiresInSeconds) =>
        $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":{{expiresInSeconds}}}""";

    private static ForecourtTokenClient CreateSut(FakeCredentialStore credentialStore, StubHttpMessageHandler handler, DateTimeOffset now) =>
        CreateSut(credentialStore, handler, new ManualTimeProvider(now));

    private static ForecourtTokenClient CreateSut(FakeCredentialStore credentialStore, StubHttpMessageHandler handler, ManualTimeProvider timeProvider) =>
        new(new HttpClient(handler), credentialStore, Microsoft.Extensions.Options.Options.Create(Options), timeProvider);

    private sealed class FakeCredentialStore(ForecourtCredential? credential) : IForecourtCredentialStore
    {
        public ForecourtCredential? TryGet() => credential;
        public void Save(ForecourtCredential newCredential) => throw new NotSupportedException();
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = new();
        public string NextResponseBody { get; set; }

        public StubHttpMessageHandler(string responseBody)
        {
            NextResponseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read the body here, before ForecourtTokenClient's `using request` disposes it.
            RequestBodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(NextResponseBody, Encoding.UTF8, "application/json"),
            };
            return response;
        }
    }
}
