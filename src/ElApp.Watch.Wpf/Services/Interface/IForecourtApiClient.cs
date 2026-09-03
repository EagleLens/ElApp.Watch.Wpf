using System.Net.Http;

namespace ElApp.Watch.Wpf.Services.Interface;

/// <summary>
/// Authenticated HTTP client for calling EagleLens backend APIs (e.g. ElApp.MainExternal.Service) using
/// the station's forecourt bearer token - attaches <c>Authorization: Bearer &lt;token&gt;</c> via
/// <see cref="IForecourtTokenClient"/> before every request. This is the reusable, permanent piece; it
/// does not know about any specific endpoint's request/response contract - that belongs to whatever
/// verify-flow integration is built on top of it.
/// </summary>
public interface IForecourtApiClient
{
    /// <param name="requestUri">Absolute URL of the backend endpoint to call.</param>
    Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken = default);

    /// <param name="requestUri">Absolute URL of the backend endpoint to call.</param>
    /// <param name="body">Serialized as the JSON request body.</param>
    Task<HttpResponseMessage> PostAsJsonAsync<TBody>(string requestUri, TBody body, CancellationToken cancellationToken = default);
}
