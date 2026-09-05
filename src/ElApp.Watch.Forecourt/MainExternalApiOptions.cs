namespace ElApp.Watch.Forecourt;

/// <summary>Bound from the "MainExternalApi" section of appsettings.json.</summary>
public sealed class MainExternalApiOptions
{
    public const string SectionName = "MainExternalApi";

    /// <summary>
    /// ElApp.MainExternal.Service's base URL for this environment/station (e.g.
    /// "https://localhost/el-mainexternal-api"). The individual endpoint paths under it are fixed across
    /// environments - see <see cref="MainExternalApiEndpoints"/> - so this is the only thing that needs to
    /// change per deployment.
    /// </summary>
    public required string BaseUrl { get; init; }
}
