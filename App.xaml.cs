using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using SerialPortTool.Services;
using SerialPortTool.ViewModels;
using System;

namespace SerialPortTool;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host;
    private Window? _window;

    /// <summary>
    /// Gets the current App instance
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection
    /// </summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Configure Serilog
        var logsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "SerialPortTool", "DebugLogs", "app-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()  // Capture all levels including Trace
            .WriteTo.File(
                path: logsPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,  // Keep last 7 days
                fileSizeLimitBytes: 50_000_000,  // 50MB per file
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Application started. Logs will be saved to: {LogPath}", logsPath);

        // Build host
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSerilog(dispose: true);
            })
            .Build();
    }

    /// <summary>
    /// Configure dependency injection services
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // Register Services
        services.AddSingleton<IBaudRateDetectorService, BaudRateDetectorService>();
        services.AddSingleton<IDataValidationService, DataValidationService>();
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<ILogFilterService, LogFilterService>();
        services.AddSingleton<IFileLoggerService, FileLoggerService>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // Register ViewModels
        services.AddTransient<MainViewModel>();

        // Register Windows
        services.AddTransient<MainWindow>();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Start the host
        await _host.StartAsync();

        // Create main window
        _window = _host.Services.GetRequiredService<MainWindow>();
        _window.Activate();
    }

}