using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ElApp.Watch.Forecourt;

/// <summary>
/// Composition-root wiring for this station's forecourt identity/auth and diagnostics/telemetry, kept
/// next to the feature it wires rather than inlined into the app's own composition root. The two are
/// separate methods (not one "AddForecourt") because a caller could plausibly want auth without
/// diagnostics, or vice versa, even though this app currently wants both.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IForecourtCredentialStore"/> (config-first, falling back to Windows
    /// Credential Manager - see <see cref="ConfigOverridingCredentialStore"/>) and the
    /// <see cref="IForecourtTokenClient"/>/<see cref="IForecourtApiClient"/> typed HttpClients used to
    /// authenticate against and call EagleLens backend APIs with this station's forecourt
    /// client_credentials.
    /// </summary>
    public static IServiceCollection AddForecourtAuth(this IServiceCollection services, IConfiguration configuration)
    {
        // Registered here (rather than only inside AddForecourtDiagnostics) so it's available regardless
        // of which of this class's two methods a caller uses - see MainExternalApiOptions/
        // MainExternalApiEndpoints, which every ElApp.MainExternal.Service endpoint is resolved from.
        services.Configure<MainExternalApiOptions>(configuration.GetSection(MainExternalApiOptions.SectionName));
        services.Configure<ForecourtAuthOptions>(configuration.GetSection(ForecourtAuthOptions.SectionName));
        services.AddSingleton<IForecourtCredentialStore>(sp => new ConfigOverridingCredentialStore(
            new WindowsCredentialManagerStore(),
            sp.GetRequiredService<IOptions<ForecourtAuthOptions>>()));
        services.AddHttpClient<IForecourtTokenClient, ForecourtTokenClient>()
            .ConfigurePrimaryHttpMessageHandler(CreateCertBypassHandler);
        services.AddHttpClient<IForecourtApiClient, ForecourtApiClient>()
            .ConfigurePrimaryHttpMessageHandler(CreateCertBypassHandler);
        return services;
    }

    /// <summary>
    /// Registers <see cref="IForecourtDiagnosticsLogger"/> (its own typed HttpClient) and the periodic
    /// <see cref="HeartbeatService"/>. Requires <see cref="AddForecourtAuth"/> to have been called too -
    /// both the diagnostics logger and the heartbeat it reports through depend on
    /// <see cref="IForecourtTokenClient"/>/<see cref="IForecourtApiClient"/>. Does not configure Serilog
    /// itself - see <see cref="ForecourtSerilogLogging.Configure"/>, which needs the
    /// <c>HostApplicationBuilder</c> directly and must run before <c>Build()</c>.
    /// </summary>
    public static IServiceCollection AddForecourtDiagnostics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ForecourtDiagnosticsOptions>()
            .Bind(configuration.GetSection(ForecourtDiagnosticsOptions.SectionName))
            .PostConfigure<IOptions<MainExternalApiOptions>>((options, mainExternalApi) =>
            {
                options.PublicLogEndpoint = MainExternalApiEndpoints.PublicLog(mainExternalApi.Value.BaseUrl);
                options.PrivateLogEndpoint = MainExternalApiEndpoints.PrivateLog(mainExternalApi.Value.BaseUrl);
            });
        services.AddHttpClient<IForecourtDiagnosticsLogger, ForecourtDiagnosticsLogger>()
            .ConfigurePrimaryHttpMessageHandler(CreateCertBypassHandler);

        services.Configure<HeartbeatOptions>(configuration.GetSection(HeartbeatOptions.SectionName));
        services.AddHostedService<HeartbeatService>();
        return services;
    }

    /// <summary>
    /// The token/API clients talk to local dev instances behind self-signed certs - a real deployment
    /// talks to properly-certified endpoints and must not carry this bypass.
    /// </summary>
    private static HttpClientHandler CreateCertBypassHandler() => new()
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    };
}
