using System.Net;
using System.Text;
using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using Xunit;

namespace ElApp.Watch.Wpf.Tests;

/// <summary>
/// See openspec change forecourt-client-credentials-auth, task 5.6. Uses a stub
/// <see cref="HttpMessageHandler"/> and a fake <see cref="IForecourtTokenClient"/> instead of a mocking
/// library (none referenced in this project) or a real network call.
/// </summary>
public class ForecourtApiClientTests
{
    [Fact]
    public async Task GetAsync_attaches_the_bearer_token_and_returns_the_response()
    {
        var tokenClient = new FakeTokenClient("token-1");
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "ok body");
        var sut = new ForecourtApiClient(new HttpClient(handler), tokenClient);

        var response = await sut.GetAsync("https://example.test/resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok body", await response.Content.ReadAsStringAsync());
        Assert.Single(handler.Requests);
        Assert.Equal("Bearer", handler.Requests[0].AuthScheme);
        Assert.Equal("token-1", handler.Requests[0].AuthToken);
        Assert.False(tokenClient.LastForceRefresh);
    }

    [Fact]
    public async Task GetAsync_on_401_forces_a_fresh_token_and_retries_once()
    {
        var tokenClient = new FakeTokenClient("token-1");
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "denied");
        var sut = new ForecourtApiClient(new HttpClient(handler), tokenClient);

        // Second call in the sequence (the retry) gets a fresh token and a success response.
        handler.ResponsesByCallIndex[1] = (HttpStatusCode.OK, "ok after retry");
        tokenClient.TokenAfterForceRefresh = "token-2";

        var response = await sut.GetAsync("https://example.test/resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok after retry", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("token-1", handler.Requests[0].AuthToken);
        Assert.Equal("token-2", handler.Requests[1].AuthToken);
    }

    [Fact]
    public async Task GetAsync_does_not_retry_a_second_time_if_the_retry_also_401s()
    {
        var tokenClient = new FakeTokenClient("token-1") { TokenAfterForceRefresh = "token-2" };
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "still denied");
        var sut = new ForecourtApiClient(new HttpClient(handler), tokenClient);

        var response = await sut.GetAsync("https://example.test/resource");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count); // one plain attempt + one forced-refresh retry, then stop
    }

    [Fact]
    public async Task PostAsJsonAsync_attaches_the_bearer_token_and_serializes_the_body()
    {
        var tokenClient = new FakeTokenClient("token-1");
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "ok body");
        var sut = new ForecourtApiClient(new HttpClient(handler), tokenClient);

        var response = await sut.PostAsJsonAsync("https://example.test/resource", new { Title = "hello" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("token-1", handler.Requests[0].AuthToken);
        // System.Net.Http.Json's JsonContent.Create defaults to camelCase (JsonSerializerDefaults.Web) -
        // fine either way, since ASP.NET Core's server-side model binding is case-insensitive.
        Assert.Contains("\"title\":\"hello\"", handler.Requests[0].Body);
    }

    private sealed class FakeTokenClient : IForecourtTokenClient
    {
        private readonly string _initialToken;

        public FakeTokenClient(string initialToken)
        {
            _initialToken = initialToken;
        }

        public bool LastForceRefresh { get; private set; }
        public string? TokenAfterForceRefresh { get; set; }

        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            LastForceRefresh = forceRefresh;
            return Task.FromResult(forceRefresh && TokenAfterForceRefresh is not null ? TokenAfterForceRefresh : _initialToken);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string? AuthScheme, string? AuthToken, string? Body);

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body) _defaultResponse;

        public List<CapturedRequest> Requests { get; } = new();
        public Dictionary<int, (HttpStatusCode Status, string Body)> ResponsesByCallIndex { get; } = new();

        public StubHttpMessageHandler(HttpStatusCode defaultStatus, string defaultBody)
        {
            _defaultResponse = (defaultStatus, defaultBody);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.Method, request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter, body));

            var (status, responseBody) = ResponsesByCallIndex.TryGetValue(index, out var overridden) ? overridden : _defaultResponse;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8),
            };
        }
    }
}
