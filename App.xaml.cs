using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using SerialPortTool.Services;
using SerialPortTool.ViewModels;
using System;
using System.Threading.Tasks;

namespace SerialPortTool;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private readonly ServiceProvider _services;
    private Window? _window;
    private bool _isClosing;

    /// <summary>
    /// Gets the current App instance
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Gets the service provider for dependency injection
    /// </summary>
    public IServiceProvider Services => _services;

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
            .MinimumLevel.Information()
            .WriteTo.File(
                path: logsPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,  // Keep last 7 days
                fileSizeLimitBytes: 50_000_000,  // 50MB per file
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("Application started. Logs will be saved to: {LogPath}", logsPath);

        // Register global unhandled exception handlers to prevent silent crashes
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled exception. IsTerminating: {IsTerminating}", e.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved(); // Prevent process termination
        };

        // Build a lightweight DI container for the desktop app.
        var services = new ServiceCollection();
        ConfigureServices(services);
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddSerilog(dispose: true);
        });

        _services = services.BuildServiceProvider();
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
        services.AddSingleton<ITuningProtocolService, TuningProtocolService>();
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
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Create main window
        _window = _services.GetRequiredService<MainWindow>();
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // Prevent re-entrance when we call _window.Close() below
        if (_isClosing) return;
        _isClosing = true;

        // Prevent the window from closing immediately
        args.Handled = true;

        try
        {
            // Dispose ViewModel (unsubscribes events - fast, non-blocking)
            if (_window is MainWindow mainWindow)
            {
                mainWindow.ViewModel?.Dispose();
            }

            // Run all cleanup on a thread pool thread with an overall timeout
            var cleanupTask = Task.Run(async () =>
            {
                try
                {
                    await _services.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error disposing services during shutdown");
                }
            });

            // Wait for cleanup with a hard timeout
            if (!cleanupTask.Wait(TimeSpan.FromSeconds(5)))
            {
                Log.Warning("Shutdown cleanup timed out after 5 seconds, forcing exit");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during shutdown cleanup");
        }
        finally
        {
            Log.CloseAndFlush();
            // Force exit the application
            Environment.Exit(0);
        }
    }

}
