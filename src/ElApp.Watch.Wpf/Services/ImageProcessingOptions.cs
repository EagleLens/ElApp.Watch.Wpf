using ElApp.Watch.Forecourt;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Not bound from appsettings.json - <see cref="ProcessImageEndpoint"/> is computed from
/// <see cref="MainExternalApiOptions.BaseUrl"/> via <see cref="MainExternalApiEndpoints"/>, see App.xaml.cs.
/// </summary>
public sealed class ImageProcessingOptions
{
    /// <summary>
    /// ElApp.MainExternal.Service's MainPrivateImageProcessingController.ProcessImage endpoint - a just-
    /// saved snapshot is posted here (multipart, bearer-token-secured) as soon as it's captured; the
    /// true/false result becomes the pump tile's green/red snapshot indicator.
    /// </summary>
    public required string ProcessImageEndpoint { get; set; }
}
