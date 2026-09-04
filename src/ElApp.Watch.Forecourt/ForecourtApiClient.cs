using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ElApp.Watch.Forecourt;

public sealed class ForecourtApiClient : IForecourtApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IForecourtTokenClient _tokenClient;

    public ForecourtApiClient(HttpClient httpClient, IForecourtTokenClient tokenClient)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
    }

    public Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default) =>
        SendWithRetryAsync(forceRefresh => CreateGetRequest(requestUri), cancellationToken);

    public Task<HttpResponseMessage> PostAsJsonAsync<TBody>(string requestUri, TBody body, CancellationToken cancellationToken = default) =>
        SendWithRetryAsync(forceRefresh => CreatePostRequest(requestUri, body), cancellationToken);

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<bool, HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        var response = await SendAsync(requestFactory, forceRefresh: false, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // A 401 with a token we believed was still valid means the server disagrees (e.g. revoked
        // server-side) - force a fresh token and retry once, per IForecourtTokenClient's forceRefresh
        // parameter (added for exactly this case).
        response.Dispose();
        return await SendAsync(requestFactory, forceRefresh: true, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<bool, HttpRequestMessage> requestFactory, bool forceRefresh, CancellationToken cancellationToken)
    {
        var token = await _tokenClient.GetAccessTokenAsync(forceRefresh, cancellationToken);
        using var request = requestFactory(forceRefresh);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage CreateGetRequest(string requestUri) => new(HttpMethod.Get, requestUri);

    private static HttpRequestMessage CreatePostRequest<TBody>(string requestUri, TBody body) =>
        new(HttpMethod.Post, requestUri) { Content = JsonContent.Create(body) };
}
