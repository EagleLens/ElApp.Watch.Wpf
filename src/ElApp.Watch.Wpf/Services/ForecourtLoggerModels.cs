namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Matches ElApp.Logger.Service's LogLevel enum (El.Logger.Api.Client.Models.Enums.LogLevel) - not
/// referenced directly to avoid pulling in that repo's NSwag client just for one enum's numeric values.
/// </summary>
public enum ForecourtLogLevel
{
    Fatal = 1,
    Error = 2,
    Warn = 3,
    Info = 4,
    Debug = 5,
    Trace = 6,
}

/// <summary>
/// Wire shape for LoggerPublicLoggerController.LogMessage (El.Logger.Api.Public.Client.PublicLoggerModel).
/// Property names match the server's C# model (case aside); serialized via JsonContent.Create, which
/// defaults to camelCase on the wire. ASP.NET Core's server-side model binding is case-insensitive, so
/// this binds correctly either way.
/// </summary>
public sealed class ForecourtPublicLogModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string ApplicationIdentifier { get; init; }
    public ForecourtLogLevel LogLevel { get; init; }
    public string? AddionalMessage { get; init; }
    public string? MoreInfo { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string CreatedDate { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public bool IsHandled { get; init; } = true;
    public string? Type { get; init; }
    public string? UserId { get; init; }
}

/// <summary>
/// Wire shape for LoggerPrivateLoggerController.LogMessage (El.Logger.Api.Client.Models.Models.LoggerModel).
/// </summary>
public sealed class ForecourtPrivateLogModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public required string ApplicationIdentifier { get; init; }
    public ForecourtLogLevel LogLevel { get; init; }
    public string? AddionalMessage { get; init; }
    public string? MoreInfo { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string CreatedDate { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public bool IsHandled { get; init; } = true;
    public string? Type { get; init; }
    public string? Source { get; init; }
    public string? ErrorCode { get; init; }
    public string? InternalCode { get; init; }
    public string? Host { get; init; } = Environment.MachineName;
    public bool Handled { get; init; } = true;
}
