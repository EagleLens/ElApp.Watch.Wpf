using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using ElApp.Watch.Wpf.ViewModels;
using ElApp.Watch.Wpf.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElApp.Watch.Wpf;

/// <summary>
/// Composition root: builds a generic host for configuration/DI, resolves the main window and
/// its view model, and shows it. Replaces the original template's empty App.xaml.cs.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // TEMPORARY, testing-only: `ElApp.Watch.Wpf.exe --clear-forecourt-credential` clears the real
        // Windows Credential Manager store. Note this has no effect on what the app actually reads
        // while appsettings.json's ForecourtAuth:SeedClientId/SeedClientSecret are both set - see
        // ConfigOverridingCredentialStore, which reads those directly and never consults the real store
        // in that case. Exits immediately, no window shown. Remove once a real setup/provisioning flow
        // exists.
        if (e.Args.Contains("--clear-forecourt-credential", StringComparer.OrdinalIgnoreCase))
        {
            new WindowsCredentialManagerStore().Delete();
            MessageBox.Show(
                "Forecourt credential cleared from the real Windows Credential Manager store.",
                "Forecourt credential cleared",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false);

        builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection(VisionOptions.SectionName));
        builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection(SnapshotOptions.SectionName));

        // Shared across every pump, lazily built on whichever pump's background thread needs it
        // first - see CameraSourceService/VehicleDetector for why exactly one instance is required.
        // Model paths from appsettings.json are relative (e.g. "Assets/Models/...") and must be
        // resolved against AppContext.BaseDirectory here, exactly as the original hardcoded
        // Path.Combine(AppContext.BaseDirectory, "Assets", "Models", ...) calls did - a bare
        // relative path handed to CvDnn/OnnxRuntime resolves against the process's current
        // working directory, not the app's install/output directory.
        builder.Services.AddSingleton(sp => new Lazy<VehicleDetector>(() =>
        {
            VisionOptions options = sp.GetRequiredService<IOptions<VisionOptions>>().Value;
            return new VehicleDetector(Path.Combine(AppContext.BaseDirectory, options.VehicleDetectorModelPath));
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        builder.Services.AddSingleton(sp => new Lazy<PlateReader>(() =>
        {
            VisionOptions options = sp.GetRequiredService<IOptions<VisionOptions>>().Value;
            return new PlateReader(
                Path.Combine(AppContext.BaseDirectory, options.PlateDetectorModelPath),
                Path.Combine(AppContext.BaseDirectory, options.PlateOcrModelPath));
        }, LazyThreadSafetyMode.ExecutionAndPublication));

        builder.Services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher(Dispatcher.CurrentDispatcher));
        builder.Services.AddSingleton<ISnapshotService, SnapshotService>();
        builder.Services.AddSingleton<ICameraSourceService, CameraSourceService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        // Forecourt auth: see openspec change forecourt-client-credentials-auth. The token/API clients
        // talk to local dev instances behind self-signed certs, hence the cert-bypass handler below - a
        // real deployment talks to properly-certified endpoints and must not carry this bypass.
        builder.Services.Configure<ForecourtAuthOptions>(builder.Configuration.GetSection(ForecourtAuthOptions.SectionName));
        // ConfigOverridingCredentialStore wraps the real Windows-Credential-Manager-backed store: while
        // appsettings.json's ForecourtAuth:SeedClientId/SeedClientSecret are both set, every read
        // returns them directly (Windows Credential Manager isn't consulted at all in that case) - see
        // that type for why. Real deployments should take WindowsCredentialManagerStore directly once a
        // real provisioning/setup flow exists and this override is removed.
        builder.Services.AddSingleton<IForecourtCredentialStore>(sp => new ConfigOverridingCredentialStore(
            new WindowsCredentialManagerStore(),
            sp.GetRequiredService<IOptions<ForecourtAuthOptions>>()));
        builder.Services.AddHttpClient<IForecourtTokenClient, ForecourtTokenClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });
        builder.Services.AddHttpClient<IForecourtApiClient, ForecourtApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });

        // Reports this station's events to ElApp.MainExternal.Service so the platform admin finds out
        // what's going on even though nobody is watching this unattended station locally - see
        // IForecourtDiagnosticsLogger. Permanent, ongoing behavior (not a testing aid).
        builder.Services.Configure<ForecourtDiagnosticsOptions>(builder.Configuration.GetSection(ForecourtDiagnosticsOptions.SectionName));
        builder.Services.AddHttpClient<IForecourtDiagnosticsLogger, ForecourtDiagnosticsLogger>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            });

        _host = builder.Build();
        _host.Start();

        await LogApplicationStartedAsync();
        await RunForecourtStartupTestCallIfConfiguredAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = viewModel;
        viewModel.Start(Path.Combine(AppContext.BaseDirectory, "Assets"));
        mainWindow.Show();
    }

    /// <summary>
    /// Permanent startup instrumentation: reports that this station started, via
    /// <see cref="IForecourtDiagnosticsLogger"/> - which endpoint actually carries the entry (public if
    /// the station cannot currently authenticate, private/attributed to the customer if it can) is
    /// <see cref="ForecourtDiagnosticsLogger"/>'s own decision, not made here. Unlike the credential
    /// seeding and smoke-test helpers below, this is real, ongoing application behavior, not a testing aid.
    /// </summary>
    private async Task LogApplicationStartedAsync()
    {
        var diagnosticsLogger = _host!.Services.GetRequiredService<IForecourtDiagnosticsLogger>();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        await diagnosticsLogger.LogAsync(
            ForecourtLogLevel.Info,
            "Forecourt Watch station started",
            $"ElApp.Watch.Wpf {version} started on {Environment.MachineName}.");
    }

    /// <summary>
    /// TEMPORARY, testing-only (see ForecourtAuthOptions.StartupTestRequestUrl): proves the
    /// client_credentials -> bearer token -> API call path works end to end by making one authenticated
    /// GET and showing the result. Remove once a real verify-flow integration exists.
    /// </summary>
    private async Task RunForecourtStartupTestCallIfConfiguredAsync()
    {
        var options = _host!.Services.GetRequiredService<IOptions<ForecourtAuthOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.StartupTestRequestUrl))
        {
            return;
        }

        var diagnosticsLogger = _host.Services.GetRequiredService<IForecourtDiagnosticsLogger>();

        try
        {
            var apiClient = _host.Services.GetRequiredService<IForecourtApiClient>();
            using var response = await apiClient.GetAsync(options.StartupTestRequestUrl);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                await diagnosticsLogger.LogAsync(
                    ForecourtLogLevel.Error,
                    "Forecourt startup test call returned a non-success status",
                    $"Status {(int)response.StatusCode} {response.StatusCode}",
                    body);
            }

            MessageBox.Show(
                $"Status: {(int)response.StatusCode} {response.StatusCode}\n\n{body}",
                "Forecourt startup test call",
                MessageBoxButton.OK,
                response.IsSuccessStatusCode ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            await diagnosticsLogger.LogAsync(
                ForecourtLogLevel.Error, "Forecourt startup test call threw", ex.Message, ex.ToString());
            MessageBox.Show(ex.ToString(), "Forecourt startup test call failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
