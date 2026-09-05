namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Wire shape for ElApp.MainExternal.Service's ProcessImage endpoint - mirrors
/// El.Main.Service's FcApiResponseOfVehicleImageResults/VehicleImageResults/VehicleProcessingResult
/// field-for-field (only Data.Result is actually used here). Not referencing that service's own
/// generated client library directly for one response shape - same reasoning as
/// ElApp.Watch.Forecourt's ForecourtLoggerModels mirroring Logger.Service's wire contracts locally
/// rather than pulling in that service's client. JSON keys are lowercase (its server-side JsonProperty
/// attributes spell them out explicitly) - deserialize with case-insensitive matching, since
/// HttpContent.ReadFromJsonAsync's default JsonSerializerOptions is case-sensitive, unlike ASP.NET
/// Core's own default request/response (de)serialization.
/// </summary>
public sealed class ProcessImageResponse
{
    public bool IsSuccess { get; init; }
    public VehicleImageResultData? Data { get; init; }
}

public sealed class VehicleImageResultData
{
    public string? Reg { get; init; }
    public VehicleProcessingResult Result { get; init; }
    public string? Warnings { get; init; }
    public string? Remarks { get; init; }
}

/// <summary>Matches El.Main.Api.Client.Models.Enums.VehicleProcessingResult's numeric values exactly.</summary>
public enum VehicleProcessingResult
{
    Valid = 1,
    Invalid = 2,
    Warning = 3,
}
