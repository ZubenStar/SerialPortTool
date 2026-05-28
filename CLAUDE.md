# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SerialPortTool** is a modern Windows desktop serial port debugging tool built with WinUI 3 and .NET 9. It supports multi-port simultaneous monitoring, real-time log filtering, intelligent data analysis, and advanced features for professional serial communication debugging.

**Tech Stack**: WinUI 3 (Windows App SDK 1.6), .NET 9, MVVM (CommunityToolkit.Mvvm 8.2.2), System.IO.Ports 9.0, Serilog 4.0, Microsoft.Extensions.DependencyInjection 9.0

**Target framework**: `net9.0-windows10.0.22621.0`, min platform `10.0.17763.0` (Windows 10 1809). Single platform: `x64`.

> Note: as of v1.8.5 the app uses a lightweight `ServiceCollection` directly — no `Microsoft.Extensions.Hosting`, no `WinUIEx`, no `Newtonsoft.Json`. The `README.md` is out-of-date on its dependency list; trust `SerialPortTool.csproj`.

## Build Commands

### Using Visual Studio (Recommended)
```powershell
# Restore packages
# Right-click solution → "Restore NuGet Packages"

# Build
# Press Ctrl+Shift+B or "Build" → "Build Solution"

# Run with debugging
# Press F5

# Run without debugging
# Press Ctrl+F5
```

### Using Command Line
```powershell
# Restore dependencies
dotnet restore

# Build for Release
dotnet build --configuration Release

# Build for specific platform
dotnet build --configuration Release --runtime win-x64 --self-contained true

# Run the application
dotnet run

# Publish self-contained package
dotnet publish --configuration Release --runtime win-x64 --self-contained true --output publish/x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishSingleFile=false
```

### Testing
There is currently no automated test suite. Manual testing involves:
- Opening multiple serial ports simultaneously
- Testing data send/receive at various baud rates
- Verifying log filtering with regex patterns
- Checking performance with high-throughput data (1000+ logs/second)

## Project Architecture

### MVVM Architecture with Dependency Injection

The application follows a layered MVVM architecture:

```
Views (XAML) ←→ ViewModels ←→ Services ←→ Hardware/Infrastructure
```

**Key Architectural Patterns**:

1. **Dependency Injection**: All services are registered in `App.xaml.cs:ConfigureServices()` as `AddSingleton`; `MainViewModel` and `MainWindow` are `AddTransient`. The DI container is a plain `ServiceCollection` (not a `Host`). Services are injected into ViewModels via constructor injection.

2. **Event-Driven Communication**: Services use events to notify ViewModels of state changes (e.g., `DataReceived`, `PortStateChanged`, `ErrorOccurred`). ViewModels dispatch events to UI thread using `DispatcherQueue`.

3. **Async-First Design**: All I/O operations (serial port, file writing) use async/await to prevent UI blocking.

### Core Service Layer

The service layer implements the business logic and hardware communication:

- **`ISerialPortService` / `SerialPortService`**:
  - Manages multiple concurrent serial port connections
  - Handles data send/receive operations
  - Implements automatic reconnection logic
  - Integrates with baud rate detection and data validation services
  - **Key Pattern**: Uses concurrent dictionary to manage multiple port instances, each with independent event handlers

- **`IBaudRateDetectorService` / `BaudRateDetectorService`**:
  - Analyzes incoming data patterns to detect incorrect baud rate
  - Tracks data quality metrics (error rate, pattern consistency)
  - Provides automatic baud rate correction suggestions
  - **Integration**: Called by SerialPortService on every data reception

- **`IDataValidationService` / `DataValidationService`**:
  - Validates data integrity and quality in real-time
  - Detects garbage data and encoding issues
  - Provides quality scores and recommendations
  - **Pattern**: Uses statistical analysis and pattern matching for validation

- **`ILogFilterService` / `LogFilterService`**:
  - Implements high-performance regex-based log filtering
  - **Critical Performance Pattern**: Caches compiled Regex objects in `ConcurrentDictionary` with LRU eviction (max 50 patterns, clear half when full)
  - Regex timeout protection: 100ms timeout prevents UI freezing from complex patterns
  - Supports multiple filter types: Text, Regex, LogLevel, PortName

- **`IFileLoggerService` / `FileLoggerService`**:
  - Async batch file writing with queue-based buffering
  - **Critical Performance Pattern**: Uses `ConcurrentQueue` with periodic flushing (100ms interval or 100 items batch)
  - StreamWriter buffer size: 65536 bytes for optimal I/O
  - Background thread writes to prevent UI blocking

- **`ISettingsService` / `SettingsService`**:
  - Persists user preferences and serial port configurations
  - Loads/saves application state
  - **Concurrency Pattern** (v1.8.10): uses a file lock to serialize concurrent writes to `settings.json`, preventing corruption when the tuning watcher and a port-open run simultaneously

- **`ITuningProtocolService` / `TuningProtocolService`** (added v1.8.7):
  - Sends `.bin` firmware/tuning files over the wire using a JSON-described protocol (e.g. `mic-tota-tuning.json` at repo root is a sample descriptor)
  - Loads a `TuningProtocolDescriptor` from JSON, packs the bin payload, and broadcasts to one or all open COM ports
  - **Key Pattern** (v1.8.9): each COM port has its own send worker so concurrent multi-port sends don't serialize
  - Checks `IsPortOpen` before sending to avoid failures while a port is reconnecting

### ViewModel Layer

- **`MainViewModel`**:
  - Coordinates all serial port operations
  - Manages log collection with `RangeObservableCollection` for batch UI updates
  - Implements search history and filter management
  - **Performance Critical**: Uses pre-allocated capacity lists and batch operations to reduce UI notification overhead
  - **Max Display Logs**: 2000 (increased from 1000 for better visibility)
  - **Batch Processing**: Groups operations to reduce collection change notifications by 50-80%

### Performance-Critical Components

The application has been heavily optimized for high-throughput scenarios (10-20x performance improvement):

1. **LogEntry Model** (`Models/LogEntry.cs`):
   - Caches formatted text in `_cachedFormattedText` field
   - Invalidates cache only when properties change via partial methods
   - Reduces string allocations by 50-70%
   - **Gotcha**: only `Content`, `PortName`, `Timestamp`, and `IsReceived` invalidate the cache (`LogEntry.cs:90-93`). `ColorHex`, `Format`, and `RawData` do **not** — they aren't part of `FormattedText`. If you add a new field that *should* appear in `FormattedText`, wire its own `partial void OnXxxChanged(...) => _cachedFormattedText = null;` — otherwise displays will silently stay stale.

2. **RangeObservableCollection** (`ViewModels/MainViewModel.cs:26-107`):
   - Custom collection supporting batch add/remove operations
   - Single `CollectionChanged` notification for batch operations
   - Exposes `AddRange(IEnumerable<T>)` and `RemoveFromStart(int)` — the latter (v1.8.3) is the efficient FIFO trim path; prefer it over `RemoveRange` for log retention
   - Critical for UI performance when adding hundreds of log entries

3. **Regex Caching** (`Services/LogFilterService.cs`):
   - Compiled regex objects cached with 100ms match timeout
   - LRU eviction: Max 50 patterns, clears 25 oldest when full
   - 5-10x performance improvement over repeated compilation

4. **Batch File Writing** (`Services/FileLoggerService.cs`):
   - Queue-based async batch writing (100 items or 100ms interval)
   - StreamWriter with 65KB buffer
   - Reused StringBuilder to reduce allocations
   - 10-20x faster than synchronous writes

5. **UI Virtualization** (`Controls/LogListView.xaml`):
   - Log view is a `ListView` wrapped in a `UserControl` (replaced the original `ItemsRepeater` in v1.8.0 to enable multi-line selection / copy)
   - **Why the UserControl wrapper**: WinUI 3 `Window` is not a `FrameworkElement`, so hosting the `ListView` inside a `UserControl` lets the `DataTemplate` use compiled `x:Bind` (~5–10× faster per-item realization than reflection-based `{Binding}` during wheel-scroll virtualization). See the comment in `LogListView.xaml:10-13`.
   - `ItemsStackPanel CacheLength="0.5"` — halves off-screen item realization vs the default 1.0
   - Empty `ItemContainerTransitions` and a minimal `Normal`/`Selected`-only visual state template — eliminates per-item layout invalidation when items parade under a stationary cursor
   - `LogEntry.FormattedText` and `ColorHex` are set once at construction, bound `OneTime`, so there is no `PropertyChanged` wiring per item
   - Selection: `Ctrl+C` copies selected logs, `Ctrl+A` selects all, right-click opens a context menu (`MenuFlyout` defined in `LogListView.xaml:24-29`)

See `WinUI3-Tech-Stack-Plan.md` for tech-stack rationale. The repository no longer ships `PERFORMANCE_OPTIMIZATIONS.md`; the version history (`version.json`) entries from v1.1.0 onward document the optimization changes.

### Log Buffer Trim Thresholds (`ViewModels/MainViewModel.cs:543-546`)

The collections are intentionally allowed to overshoot before being trimmed; trimming on every overflow caused flicker in v1.8.6 and earlier. Three related constants:

```csharp
private const int MaxDisplayLogs           = 2000;                     // target steady-state size of DisplayLogs
private const int DisplayLogTrimThreshold  = MaxDisplayLogs + 200;     // trim DisplayLogs only after exceeding 2200
private const int AllLogsTrimThreshold     = MaxDisplayLogs * 2 + 400; // 4400 — back-buffer of all unfiltered logs
private const int MaxQueuedLogEntries      = MaxDisplayLogs * 4;       // 8000 — pending-update queue cap from background threads
```

Bumping `MaxDisplayLogs` without also raising the thresholds will reintroduce the overflow-trim-overflow flicker pattern those thresholds were added to fix.

### Version Management

**Single Source of Truth**: `version.json` contains the application version and changelog.

The version flows through the build system:
1. MSBuild reads `version.json` at evaluation time using regex
2. Version automatically propagates to `.csproj` properties (`Version`, `AssemblyVersion`, `FileVersion`)
3. `UpdateManifestVersion` target updates `Package.appxmanifest` before build
4. `GenerateBuildInfo` target creates `Helpers/BuildInfo.g.cs` with UTC build timestamp

**To bump version**: Edit `version.json` only, or use:
```powershell
.\scripts\bump-version.ps1 -BumpType patch
```

**Build-time generated files**:
- `Helpers/BuildInfo.g.cs` - Auto-generated with build timestamp (do not edit manually)

## Key Code Patterns

### Adding a New Service

1. Define interface in `Services/I<ServiceName>.cs`
2. Implement in `Services/<ServiceName>.cs`
3. Register in `App.xaml.cs:ConfigureServices()` — pick the lifetime that matches the consumer:
   ```csharp
   // Services holding state shared across the app: Singleton.
   services.AddSingleton<IYourService, YourService>();

   // ViewModels and Windows: Transient (one fresh instance per resolve).
   services.AddTransient<YourViewModel>();
   ```
4. Inject via constructor in ViewModel:
   ```csharp
   public MainViewModel(IYourService yourService)
   {
       _yourService = yourService;
   }
   ```

### Handling Serial Port Events

Services raise events on background threads. ViewModels must dispatch to UI thread:

```csharp
_serialPortService.DataReceived += async (sender, e) =>
{
    await DispatcherQueue.EnqueueAsync(() =>
    {
        // Update UI-bound collections here
        Logs.Add(newLogEntry);
    });
};
```

### Batch Collection Updates

Always use `RangeObservableCollection.AddRange()` for multiple items:

```csharp
var newLogs = new List<LogEntry>(capacity: estimatedSize);
// ... populate newLogs ...
DisplayedLogs.AddRange(newLogs);  // Single UI notification
```

**Never** add items in a loop with individual notifications - this causes severe UI lag.

### Regex Filtering Performance

When filtering logs, always use the cached regex from `LogFilterService`:

```csharp
// GOOD - Uses cached compiled regex
if (_logFilterService.ShouldDisplay(logEntry)) { ... }

// BAD - Creates new Regex on every call
if (Regex.IsMatch(text, pattern)) { ... }  // Avoid!
```

### Async File Operations

File logging is async and batched. Never write directly to files:

```csharp
// GOOD - Goes through batched service
await _fileLoggerService.LogAsync(logEntry);

// BAD - Blocks UI thread
File.AppendAllText(path, logEntry.ToString());  // Never do this!
```

## Common Development Scenarios

### Adding a New Log Filter Type

1. Add enum value to `Core/Enums/FilterType.cs`
2. Update `LogFilterService.ShouldDisplay()` to handle new type
3. Add UI controls in filter panel (if needed)
4. Consider performance implications - cache expensive operations

### Adding Baud Rate Detection Patterns

Edit `Services/BaudRateDetectorService.cs`:
- Update `AnalyzeDataQuality()` for new pattern detection
- Adjust confidence thresholds in `SuggestBaudRate()`
- Test with real device data at various baud rates

## CI/CD and Release Process

### GitHub Actions Workflow

**Trigger**: Push tags matching `v*` (e.g., `v1.6.1`)

**Workflow** (`.github/workflows/release.yml`):
1. Validates `version.json` matches git tag
2. Generates `BuildInfo.g.cs` with UTC timestamp
3. Builds for x64 platform (Release configuration)
4. Publishes self-contained package (no R2R, no single-file, no trimming)
5. Creates ZIP archive with README and version.json
6. Generates release notes from `version.json` changelog
7. Creates GitHub Release with artifacts

**To release**:
1. Update `version.json` with new version and changelog
2. Commit and push
3. Create and push tag: `git tag v1.7.0 && git push origin v1.7.0`
4. GitHub Actions will build and create release automatically

## Important Constraints

- **Platform**: x64 Windows only (ARM64 support removed in v1.4.0)
- **Minimum Windows Version**: Windows 10 version 1809 (build 17763)
- **Publishing Settings**: `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` (required for WinUI 3 stability)
- **Language**: C# 12 (required for partial properties in ObservableObject)

## Debugging Tips

- **Application Logs**: Saved to `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log` (daily rolling, 7-day retention, 50 MB cap per file — configured in `App.xaml.cs:38-52`)
- **Log Level**: `Information` (configured at `App.xaml.cs:44`)
- **Global Exception Handlers** (`App.xaml.cs:57-68`, added v1.8.10): `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are both logged via Serilog. Do not remove these — they exist because the app was silently terminating on background-thread exceptions.
- **Performance Monitoring**: Use `PerformanceMonitor` helper in `Helpers/PerformanceMonitor.cs`:
  ```csharp
  using (_perfMonitor.Measure("OperationName"))
  {
      // Code to profile
  }
  _perfMonitor.LogReport();  // View statistics
  ```
- **Slow Operation Warnings**: Automatically logged if operation exceeds 100ms

## Architecture Considerations

### Multi-Threading Model

- **Serial Port Reading**: Each port has its own background read thread inside `SerialPortService`
- **Tuning Sends**: Each port has its own send worker (v1.8.9) so concurrent multi-port sends do not serialize on a single channel
- **File Writing**: Single background thread with batched queue
- **Regex Compilation**: Compiled on first use, cached for reuse
- **UI Updates**: All collection modifications must be on UI thread via `DispatcherQueue`

### Memory Management

- **Log Retention (UI)**: target ~2000 per `DisplayLogs`, hard trim at 2200 (see `DisplayLogTrimThreshold`); the unfiltered `AllLogs` back-buffer trims at 4400
- **File Logs**: Unlimited (written to disk, not memory)
- **Regex Cache**: Max 50 patterns, LRU eviction
- **String Allocations**: Minimized via cached `FormattedText` on `LogEntry` (set once at construction, bound `OneTime` from the `DataTemplate`)

### Error Handling Strategy

- Services log errors via `ILogger` (Serilog)
- Critical errors raise `ErrorOccurred` events to ViewModels
- ViewModels show user-friendly error messages in UI
- Serial port errors trigger automatic reconnection if enabled
- Regex timeout protection prevents UI freezing from malicious patterns

## Reliability Mechanisms (do not regress)

Most of these are subtle and exist because a specific bug caused a crash or storm — removing them tends to look like simplification right up to the next incident.

- **Settings file lock** (`SettingsService`, v1.8.10): writes to `settings.json` are serialized to avoid corruption when the tuning watcher and a port-open path race.
- **Reconnect cooldown** (`SerialPortService`, v1.8.10): a failed reconnect attempt enforces a backoff before retrying. Removing it produces thousands of `UnauthorizedAccessException` events when a port is yanked.
- **Port reopen retry** (`SerialPortService` / `PortInstance.Dispose`, v1.8.11): on close, `_isClosing` is reset and availability is re-checked while the OS releases the COM handle; the cleanup delay lives in `Dispose`'s `finally` so it runs even on exception paths.
- **DataValidationService queue lock** (v1.8.10): the queue inside `PortValidationState` is locked because it was being mutated concurrently from the read thread and validation worker.
- **Tuning send pre-check** (`TuningProtocolService`, v1.8.10): every send checks `IsPortOpen` first — required because tuning auto-send can fire while a port is mid-reconnect, causing `CancellationTokenSource` disposal crashes.
- **Shutdown timeout** (`App.xaml.cs:114-160`): window-close cleanup runs on a thread-pool task with a hard 5-second wall clock; on timeout the app force-exits rather than hanging on a stuck COM handle.

## Notable User-Facing Features (architectural)

Only features that change how an agent should reason about the code — version.json has the full changelog.

- **Log pause toggle** (`HEAD`, commit `a938392`): pauses log appending without stopping serial reception; batched updates resume on unpause. Anything that touches the data-flow pipeline must respect this — buffering still happens during pause, only the UI append is gated.
- **Per-port colors** (v1.7.0): each opened port gets a unique color from a 10-color palette stored on `LogEntry.ColorHex`. Surfaced via `HexColorToBrushConverter` in `LogListView.xaml`'s `DataTemplate`. New `LogEntry` fields that need a color treatment should pass through the same converter.

## Project Layout Notes

- `MainWindow.xaml` / `MainWindow.xaml.cs` live at the repo root, not in a `Views/` folder — the README's directory diagram is wrong about this.
- `Controls/LogListView.xaml` is the only custom user control; if adding new ones, follow the same `UserControl`-wrapping-a-ListView pattern so `x:Bind` keeps working under WinUI 3's `Window` ≠ `FrameworkElement` constraint.
- `Converters/` contains `BoolToVisibilityConverter`, `HexColorToBrushConverter` (per-port colors, v1.7.0), and `StringToVisibilityConverter` (search-history clear button visibility, v1.6.2). All three are registered as app-level resources in `App.xaml`.
- `Core/Enums/` contains only `ConnectionState`, `DataFormat`, `FilterType` (no `LogLevel.cs` — the project uses `Microsoft.Extensions.Logging.LogLevel`).
- `Helpers/BuildInfo.g.cs` is build-time generated by `scripts/generate-buildinfo.ps1` — do not edit manually and do not commit it; it is regenerated on every build.
- `mic-tota-tuning.json` at repo root is a sample `TuningProtocolDescriptor`, not application config.

## Data Flow Example: Receiving Serial Data

```
Hardware Serial Port (background thread)
  ↓
SerialPortService.DataReceivedHandler (validates/detects baud rate)
  ↓
DataReceived Event Raised (still background thread)
  ↓
MainViewModel Event Handler
  ↓
DispatcherQueue.EnqueueAsync (switch to UI thread)
  ↓
LogFilterService.ShouldDisplay (apply filters with cached regex)
  ↓
RangeObservableCollection.AddRange (batch update)
  ↓
ListView UI update inside Controls/LogListView.xaml (virtualized, x:Bind)
  ↓
FileLoggerService.LogAsync (async batched write to disk)
```

## Known Issues and Limitations

- **Port Close Reliability**: Windows can hold COM handles after close. Handled by retry + cleanup-delay in `finally` — see Reliability Mechanisms above before touching `SerialPortService.PortInstance.Dispose`.
- **High-Speed Data**: At extremely high baud rates (>921600), some data loss may occur. Consider increasing buffer sizes in `SerialPortService`.
- **Regex Performance**: Very complex regex patterns with backtracking can hit the 100ms timeout. Keep patterns simple for real-time filtering.

## Additional Documentation

- `SerialPortTool-Architecture-Plan.md` - Detailed architecture and design decisions
- `WinUI3-Tech-Stack-Plan.md` - Tech stack rationale and WinUI 3 specifics
- `Development-Environment-Setup-Guide.md` - Environment setup instructions
- `version.json` - Version history and changelog (single source of truth for version)
- `README.md` - User-facing overview (Chinese; partially stale on dependency list and directory structure — CLAUDE.md is the source of truth for agents)
