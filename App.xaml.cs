using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using SerialPortTool.Core.Enums;
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
    /// <summary>Settings key holding the persisted <see cref="AppThemePreference"/>.</summary>
    public const string ThemeSettingKey = "AppTheme";

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
    /// Appearance read from settings before the main window is constructed.
    /// </summary>
    /// <remarks>
    /// The read has to happen here rather than in <c>MainWindow</c>: the window needs to know the
    /// theme before <c>InitializeComponent()</c> so the first frame is already painted in the right
    /// palette. Setting <c>ElementTheme</c> after the window is shown produces a visible light-to-dark
    /// flash on every launch for dark-theme users.
    /// </remarks>
    public AppThemePreference InitialThemePreference { get; private set; } = AppThemePreference.System;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Serilog and the exception handlers are registered BEFORE InitializeComponent() on purpose,
        // for two reasons:
        //
        // 1. Application.LoadComponent() is where App.xaml and its merged dictionaries are realised,
        //    so a broken resource dictionary currently dies with nothing in the log.
        // 2. InitializeComponent() also registers the XAML compiler's generated debug handler
        //    (App.g.i.cs: "UnhandledException += (s, e) => Debugger.Break()" under
        //    DEBUG && !DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION). Handlers run in
        //    registration order, so registering ours first is what puts the exception in the log
        //    before the debugger stops the process.
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

        // XAML's own channel. AppDomain.UnhandledException below does NOT cover an exception raised by
        // the framework on the UI thread (binding evaluation, template instantiation, window
        // construction): those are routed through Application.UnhandledException, and with no
        // subscriber the process is torn down with an empty log.
        UnhandledException += (sender, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled XAML exception: {Message}", e.Message);
            Log.CloseAndFlush();
        };

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

        InitializeComponent();

        Log.Information("Application started. Logs will be saved to: {LogPath}", logsPath);

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
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<IUpdateInstallerService, UpdateInstallerService>();

        // Register ViewModels
        services.AddTransient<MainViewModel>();

        // Register Windows
        services.AddTransient<MainWindow>();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <remarks>
    /// <c>async void</c> is required — the base signature returns <c>void</c> — and the appearance
    /// read below is the only thing that runs before the window exists. It is wrapped so a settings
    /// failure can never prevent the app from starting.
    /// </remarks>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Captured while we are definitely on the UI thread. Creating a WinUI 3 Window from any other
        // thread throws, so the guard below is what keeps this async method safe.
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        await LoadInitialThemePreferenceAsync();

        if (dispatcher is not null && !dispatcher.HasThreadAccess)
        {
            // Should be unreachable: the await above captures the dispatcher context. Kept as a
            // fail-safe so a future ConfigureAwait(false) in the settings path cannot turn
            // "app fails to start" into a hard-to-diagnose crash.
            Log.Warning("Launch continuation left the UI thread; marshalling window creation back");
            dispatcher.TryEnqueue(CreateAndActivateMainWindow);
            return;
        }

        CreateAndActivateMainWindow();
    }

    private void CreateAndActivateMainWindow()
    {
        _window = _services.GetRequiredService<MainWindow>();
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private async Task LoadInitialThemePreferenceAsync()
    {
        try
        {
            var settings = _services.GetRequiredService<ISettingsService>();
            var raw = await settings.LoadSettingAsync(ThemeSettingKey, nameof(AppThemePreference.System));

            InitialThemePreference =
                Enum.TryParse<AppThemePreference>(raw, ignoreCase: true, out var parsed) &&
                Enum.IsDefined(parsed)
                    ? parsed
                    : AppThemePreference.System;
        }
        catch (Exception ex)
        {
            // Falling back to the system appearance keeps the app usable; the user can re-pick.
            InitialThemePreference = AppThemePreference.System;
            Log.Warning(ex, "Could not read the saved appearance preference; using the system appearance");
        }
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
