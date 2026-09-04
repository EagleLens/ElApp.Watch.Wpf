using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ElApp.Watch.Forecourt;

public sealed class ForecourtTokenClient : IForecourtTokenClient
{
    private readonly HttpClient _httpClient;
    private readonly IForecourtCredentialStore _credentialStore;
    private readonly ForecourtAuthOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CachedToken? _cachedToken;

    public ForecourtTokenClient(
        HttpClient httpClient,
        IForecourtCredentialStore credentialStore,
        IOptions<ForecourtAuthOptions> options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _credentialStore = credentialStore;
        _options = options.Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryGetUnexpiredCachedToken(out var cachedValue))
        {
            return cachedValue;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (!forceRefresh && TryGetUnexpiredCachedToken(out cachedValue))
            {
                return cachedValue;
            }

            var credential = _credentialStore.TryGet() ??
                throw new InvalidOperationException("No forecourt credential has been provisioned on this station yet.");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = credential.ClientId,
                    ["client_secret"] = credential.ClientSecret,
                }),
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken) ??
                throw new InvalidOperationException("The token endpoint returned an empty response.");

            var now = _timeProvider.GetUtcNow();
            _cachedToken = new CachedToken(payload.AccessToken, now.AddSeconds(payload.ExpiresIn));
            return _cachedToken.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool TryGetUnexpiredCachedToken(out string accessToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cachedToken is { } cached && cached.ExpiresAtUtc - _options.RefreshMargin > now)
        {
            accessToken = cached.AccessToken;
            return true;
        }

        accessToken = string.Empty;
        return false;
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAtUtc);

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}
