using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ElApp.Watch.Forecourt;

/// <summary>
/// This station's local IPv4 address, for the log entries' <c>Host</c> field - identifies which physical
/// station sent an entry on the local network, independent of <see cref="Environment.MachineName"/>.
/// Resolved once per process (a station's network config doesn't change mid-run) via network interface
/// enumeration rather than DNS - reliable even without a configured/working local DNS setup.
/// </summary>
public static class LocalNetworkInfo
{
    public static readonly string? LocalIpAddress = TryGetLocalIpAddress();

    private static string? TryGetLocalIpAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Select(addr => addr.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(ip))
                .FirstOrDefault()
                ?.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// True for a 169.254.0.0/16 (APIPA) self-assigned address - what Windows hands an adapter that's
    /// "Up" but never actually got a real one (e.g. plugged in but no DHCP/link), not a usable LAN
    /// address. Without this exclusion, a disconnected adapter enumerated before the real one would win.
    /// </summary>
    private static bool IsLinkLocal(System.Net.IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}

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
    public string? Host { get; init; } = LocalNetworkInfo.LocalIpAddress;
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
    public string? Host { get; init; } = LocalNetworkInfo.LocalIpAddress;
    public bool Handled { get; init; } = true;
}
