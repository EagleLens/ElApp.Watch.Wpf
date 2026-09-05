using System.Net.Sockets;

namespace ElApp.Watch.Wpf.Services;

/// <summary>
/// Startup gate for this station: ElApp.Watch.Wpf only does anything useful against cloud-hosted EagleLens
/// services, so App.xaml.cs checks this before building the host or loading vision models and exits with a
/// clear error if it fails, rather than starting into a station that can't reach any of its backends.
/// </summary>
public static class InternetConnectivityChecker
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    // Cloudflare's public DNS resolver: a well-known, highly-available host that answers on port 53 from
    // anywhere with real internet access. A raw TCP connect - rather than an ICMP ping (routinely blocked
    // by network policy) or a DNS lookup (a captive portal can intercept and answer that locally without
    // real internet access behind it) - is the cheapest reliable signal that this machine can actually
    // reach the internet.
    private const string ProbeHost = "1.1.1.1";
    private const int ProbePort = 53;

    /// <returns><c>true</c> if a TCP connection to the probe host succeeded within
    /// <see cref="ConnectTimeout"/>; <c>false</c> for any failure (no route, timeout, DNS/proxy
    /// interference, etc.) - this never throws.</returns>
    public static async Task<bool> IsConnectedAsync()
    {
        try
        {
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(ConnectTimeout);
            await client.ConnectAsync(ProbeHost, ProbePort, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
