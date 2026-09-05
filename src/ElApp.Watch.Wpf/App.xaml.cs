using ElApp.Watch.Forecourt;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services;
using ElApp.Watch.Wpf.Services.Interface;
using ElApp.Watch.Wpf.ViewModels;
using ElApp.Watch.Wpf.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

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

        // `ElApp.Watch.Wpf.exe --clear-forecourt-credential` clears this station's stored credential from
        // Windows Credential Manager - useful when decommissioning or re-provisioning a station. Has no
        // effect while appsettings.json's ForecourtAuth:ClientId/ClientSecret are both set - see
        // ConfigOverridingCredentialStore, which reads those directly and never consults the real store
        // in that case. Exits immediately, no window shown.
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

        // Shown for the span between process start and the main window appearing - covers the internet
        // check below and the host/DI startup that follows it, both of which take a visible moment.
        var splash = new SplashWindow();
        splash.Show();

        // This station is useless without connectivity to EagleLens's cloud-hosted services, so fail fast
        // with a clear message rather than starting into a half-working state (vision models loaded,
        // cameras opened, then every backend call failing) - see InternetConnectivityChecker.
        splash.SetStatus("Checking internet connection...");
        if (!await InternetConnectivityChecker.IsConnectedAsync())
        {
            splash.Close();
            MessageBox.Show(
                "ElApp.Watch.Wpf requires an active internet connection to start, and none could be " +
                "detected on this station. Please check the network connection and restart the application.",
                "No internet connection",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        splash.SetStatus("Starting...");

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.SetBasePath(AppContext.BaseDirectory);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false);

        builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection(VisionOptions.SectionName));
        builder.Services.Configure<SnapshotOptions>(builder.Configuration.GetSection(SnapshotOptions.SectionName));

        // ProcessImageEndpoint isn't bound from appsettings.json - it's resolved from MainExternalApi:
        // BaseUrl (registered by AddForecourtAuth below) via MainExternalApiEndpoints, same as
        // ForecourtDiagnosticsOptions's own endpoints - see ServiceCollectionExtensions.
        builder.Services.AddOptions<ImageProcessingOptions>()
            .PostConfigure<IOptions<MainExternalApiOptions>>((options, mainExternalApi) =>
                options.ProcessImageEndpoint = MainExternalApiEndpoints.ProcessImage(mainExternalApi.Value.BaseUrl));

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

        // Forecourt identity/auth and diagnostics/telemetry: see ElApp.Watch.Forecourt (openspec change
        // forecourt-client-credentials-auth) - neither has any WPF dependency, so both live in their own
        // class library with their own DI wiring; this composition root just calls it.
        builder.Services.AddForecourtAuth(builder.Configuration);
        builder.Services.AddForecourtDiagnostics(builder.Configuration);

        // Serilog, matching the platform-wide pattern used by every other El* service (see e.g.
        // ElApp.AuthService.Web's Mvc/Configurations/SerilogAndFcLogger.cs): replaces the logging
        // backend outright, so any existing or future Log.XXX(...) call anywhere in this app's own code
        // is automatically forwarded to IForecourtDiagnosticsLogger - no call site needs to know this
        // pipeline exists. Must run before Build() (Serilog convention) - the forwarding sink only
        // resolves _host.Services lazily, once an actual log event needs forwarding, well after
        // Build()/Start() complete.
        ForecourtSerilogLogging.Configure(builder, () => _host?.Services);

        _host = builder.Build();
        _host.Start();

        await LogApplicationStartedAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = viewModel;
        viewModel.Start(Path.Combine(AppContext.BaseDirectory, "Assets"));

        // Showing splash first made WPF implicitly treat it as Application.MainWindow (the default for
        // whichever window is shown first) - overriding that here is required so ShutdownMode=
        // OnMainWindowClose triggers off the real main window, not off splash.Close() below.
        MainWindow = mainWindow;
        mainWindow.Show();
        splash.Close();
    }

    /// <summary>
    /// Reports that this station started, via <see cref="IForecourtDiagnosticsLogger"/> - which endpoint
    /// actually carries the entry (public if the station cannot currently authenticate, private/attributed
    /// to the customer if it can) is <see cref="ForecourtDiagnosticsLogger"/>'s own decision, not made here.
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

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
