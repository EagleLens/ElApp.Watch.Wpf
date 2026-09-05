namespace ElApp.Watch.Wpf.Services;

/// <summary>Bound from the "ImageProcessing" section of appsettings.json.</summary>
public sealed class ImageProcessingOptions
{
    public const string SectionName = "ImageProcessing";

    /// <summary>
    /// ElApp.MainExternal.Service's MainPrivateImageProcessingController.ProcessImage endpoint - a just-
    /// saved snapshot is posted here (multipart, bearer-token-secured) as soon as it's captured; the
    /// true/false result becomes the pump tile's green/red snapshot indicator.
    /// </summary>
    public required string ProcessImageEndpoint { get; init; }
}
