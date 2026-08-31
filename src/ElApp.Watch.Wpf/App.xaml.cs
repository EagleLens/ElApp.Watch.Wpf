using System.IO;
using System.Windows;
using System.Windows.Threading;
using ElApp.Watch.Vision;
using ElApp.Watch.Wpf.Services;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _host = builder.Build();
        _host.Start();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var viewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainWindow.DataContext = viewModel;
        viewModel.Start(Path.Combine(AppContext.BaseDirectory, "Assets"));
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
