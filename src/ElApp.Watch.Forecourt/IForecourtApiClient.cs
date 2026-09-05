namespace ElApp.Watch.Forecourt;

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

    /// <summary>
    /// Posts <paramref name="fileContent"/> as a multipart/form-data file upload, under form field name
    /// "image" (must match the target endpoint's <c>IFormFile</c> parameter name for ASP.NET Core's
    /// default model binding to pick it up).
    /// </summary>
    /// <param name="requestUri">Absolute URL of the backend endpoint to call.</param>
    /// <param name="fileContent">
    /// The whole file, already in memory - not a Stream, so this can be sent twice without extra care if
    /// a 401 triggers a forced-token-refresh retry (a Stream would need rewinding/recreating).
    /// </param>
    Task<HttpResponseMessage> PostFileAsync(string requestUri, byte[] fileContent, string fileName, string contentType, CancellationToken cancellationToken = default);
}
