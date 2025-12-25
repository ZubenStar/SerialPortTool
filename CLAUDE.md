# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**SerialPortTool** is a modern Windows desktop serial port debugging tool built with WinUI 3 and .NET 9. It supports multi-port simultaneous monitoring, real-time log filtering, intelligent data analysis, and advanced features for professional serial communication debugging.

**Tech Stack**: WinUI 3, .NET 9, MVVM (CommunityToolkit.Mvvm), System.IO.Ports, Serilog, Microsoft.Extensions.DependencyInjection

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

1. **Dependency Injection**: All services are registered in `App.xaml.cs:ConfigureServices()` using Microsoft.Extensions.DependencyInjection. Services are injected into ViewModels via constructor injection.

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

2. **RangeObservableCollection** (`ViewModels/MainViewModel.cs:23-73`):
   - Custom collection supporting batch add/remove operations
   - Single `CollectionChanged` notification for batch operations
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

5. **UI Virtualization** (`MainWindow.xaml:212-240`):
   - Uses `ItemsRepeater` with `StackLayout` for efficient rendering
   - TextBlock optimization: `OpticalMarginAlignment="None"`, `TextLineBounds="Tight"`
   - Only renders visible items (virtualized scrolling)

See `PERFORMANCE_OPTIMIZATIONS.md` for detailed performance documentation.

### Version Management

**Single Source of Truth**: `version.json` contains the application version and changelog.

The version flows through the build system:
1. MSBuild reads `version.json` at evaluation time using regex
2. Version automatically propagates to `.csproj` properties (`Version`, `AssemblyVersion`, `FileVersion`)
3. `UpdateManifestVersion` target updates `Package.appxmanifest` before build
4. `GenerateBuildInfo` target creates `Helpers/BuildInfo.g.cs` with UTC build timestamp

**To bump version**: Edit `version.json` only, or use:
```powershell
.\scripts\bump-version.ps1 -NewVersion "1.7.0"
```

**Build-time generated files**:
- `Helpers/BuildInfo.g.cs` - Auto-generated with build timestamp (do not edit manually)

## Key Code Patterns

### Adding a New Service

1. Define interface in `Services/I<ServiceName>.cs`
2. Implement in `Services/<ServiceName>.cs`
3. Register in `App.xaml.cs:ConfigureServices()`:
   ```csharp
   services.AddSingleton<IYourService, YourService>();
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

### Changing Max Displayed Logs

The limit is in `ViewModels/MainViewModel.cs`:
```csharp
private const int MaxDisplayLogs = 2000;  // Adjust this constant
```

Higher values increase memory usage but provide more visible history. Consider impact on UI rendering performance.

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

- **Application Logs**: Saved to `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log`
- **Log Level**: Verbose (captures all trace/debug logs)
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

- **Serial Port Reading**: Each port has dedicated background thread for reading
- **File Writing**: Single background thread with batched queue
- **Regex Compilation**: Compiled on first use, cached for reuse
- **UI Updates**: All collection modifications must be on UI thread via `DispatcherQueue`

### Memory Management

- **Log Retention**: Max 2000 logs per port in UI (older logs automatically trimmed)
- **File Logs**: Unlimited (written to disk, not memory)
- **Regex Cache**: Max 50 patterns, LRU eviction
- **String Allocations**: Minimized via cached formatted text in LogEntry

### Error Handling Strategy

- Services log errors via `ILogger` (Serilog)
- Critical errors raise `ErrorOccurred` events to ViewModels
- ViewModels show user-friendly error messages in UI
- Serial port errors trigger automatic reconnection if enabled
- Regex timeout protection prevents UI freezing from malicious patterns

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
ItemsRepeater UI Update (virtualized rendering)
  ↓
FileLoggerService.LogAsync (async batched write to disk)
```

## Known Issues and Limitations

- **Port Close Reliability**: Windows sometimes holds COM port handles. The service implements retry logic with up to 5 attempts and GC fallback.
- **High-Speed Data**: At extremely high baud rates (>921600), some data loss may occur. Consider increasing buffer sizes in `SerialPortService`.
- **Regex Performance**: Very complex regex patterns with backtracking can hit the 100ms timeout. Keep patterns simple for real-time filtering.

## Additional Documentation

- `SerialPortTool-Architecture-Plan.md` - Detailed architecture and design decisions
- `PERFORMANCE_OPTIMIZATIONS.md` - Performance optimization documentation with benchmarks
- `Development-Environment-Setup-Guide.md` - Environment setup instructions
- `version.json` - Version history and changelog
