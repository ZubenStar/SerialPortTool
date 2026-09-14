# AGENTS.md

> Guidance for AI coding agents (CodeBuddy / Claude Code / Cursor / Copilot / Codex …) working in this repository.
>
> **This file is the single source of truth for developers and agents.** `CLAUDE.md` is a thin pointer to this file.
> Keep the repository documentation set small: `README.md` (users), `AGENTS.md` (this file, developers/agents), `version.json` (version + changelog). Historical design notes are deliberately **not** kept in the repo — git history is the archive.

---

## 0. Documentation Sync Requirement (MANDATORY)

**Any change to code must be accompanied by updating the related documentation in the very same change.**
Documentation is part of the deliverable: a change that makes the documentation wrong is an incomplete change.

| If you change … | You must also update … |
| --- | --- |
| User-visible behaviour, features, UI text, requirements, packaging | `README.md` |
| Architecture, services, DI registrations, threading model, performance/reliability mechanisms, build & version flow, coding conventions | `AGENTS.md` (this file) |
| Anything that ships (feature, fix, refactor, perf, build/pipeline change) | `version.json` — add/adjust a changelog entry (use `scripts/bump-version.ps1` when cutting a release) |
| Release / publish pipeline | `.github/workflows/release.yml` **and** the CI/CD section below |
| The tuning protocol descriptor format or the sample | `mic-tota-tuning.json` **and** the Tuning section below |

Hard rules:

1. **Never** leave a documented path, type, method, constant, or version stale. If you rename/move/delete something referenced in a doc, fix the reference in the same commit.
2. Sections marked **"do not regress"** describe mechanisms that exist because a specific bug caused a crash or a storm. Do not remove or "simplify" them without an explicit, documented reason.
3. **Do not add new top-level `*.md` files.** Extend `README.md` (user-facing) or `AGENTS.md` (developer/agent-facing) instead. If a plan/spec document is needed for a single task, do not commit it.
4. If a change cannot be verified by `dotnet build`, say so explicitly in the summary.

---

## Project Overview

**SerialPortTool (串口工具)** is a Windows desktop serial-port debugging / monitoring tool built with WinUI 3 and .NET 9. It focuses on multi-port simultaneous monitoring, real-time log filtering, baud-rate mismatch detection, and pushing tuning/TOTA firmware payloads over the wire.

**Tech stack** (trust `SerialPortTool.csproj`, nothing else):

- WinUI 3 (Windows App SDK `1.6.241114003`, self-contained), C# 12, .NET 9
- MVVM (`CommunityToolkit.Mvvm 8.2.2`)
- `System.IO.Ports` 9.0.0
- Serilog 4.0.0 + `Serilog.Sinks.File` 5.0.0 + `Serilog.Extensions.Logging` 8.0.0
- `Microsoft.Extensions.DependencyInjection` 9.0.0 + `Microsoft.Extensions.Logging` 9.0.0
- `Microsoft.Windows.SDK.BuildTools` 10.0.26100.1742

**Deliberately absent** (removed in v1.8.5 — do not re-add casually): `Microsoft.Extensions.Hosting`, `WinUIEx`, `Newtonsoft.Json`, `CsvHelper`, `CommunityToolkit.WinUI.Controls.*`. The DI container is a plain `ServiceCollection`.

**Target framework**: `net9.0-windows10.0.22621.0`, min platform `10.0.17763.0` (Windows 10 1809). Single platform: `x64`. Packaging: **unpackaged** (`WindowsPackageType=None`).

> `Package.appxmanifest` is effectively vestigial for this unpackaged build: it is still updated by the build (see Version Management) but its `Assets\*.png` visual-asset references do not exist and are not validated. Do not "fix" the build by deleting the manifest without checking the MSIX tooling targets first.

---

## Build / Run

### Visual Studio 2022 (17.8+)
`SerialPortTool.sln` → restore NuGet → `Ctrl+Shift+B` to build → `F5` to debug.

### Command line (PowerShell)
```powershell
dotnet restore
dotnet build --configuration Release
dotnet run

# Self-contained publish (the settings the release pipeline uses)
dotnet publish --configuration Release --runtime win-x64 --self-contained true `
  --output publish/x64 -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:PublishSingleFile=false
```

**Prerequisites**: .NET 9 SDK, Windows App SDK 1.6 runtime/build tools, Windows 10 1809+ (Windows 11 recommended). The build invokes PowerShell scripts, so it must run on Windows.

### Testing
There is **no automated test suite**. Verification is manual:

- open several ports simultaneously (open/close/reopen, including with a wrong baud rate first),
- send/receive at various baud rates, including hex mode,
- exercise log filtering (plain text + regex) under a high-throughput stream,
- run a tuning broadcast across ≥2 ports.

---

## Repository Layout

```
SerialPortTool/
├── App.xaml / App.xaml.cs           # Entry point: Serilog, DI container, global exception handlers, shutdown
├── MainWindow.xaml / .cs            # Main window — lives at the ROOT (there is no Views/ folder)
├── Package.appxmanifest             # Kept for MSIX tooling; unused assets referenced (see note above)
├── SerialPortTool.csproj / .sln
├── version.json                     # Single source of truth: version + changelog
├── mic-tota-tuning.json             # SAMPLE TuningProtocolDescriptor (not application config)
├── Assets/Images/                   # logo.ico, logo.png
├── Controls/LogListView.xaml(.cs)   # The only custom UserControl (virtualized log list)
├── Converters/                      # BoolToVisibility + InverseBoolToVisibility (one file),
│                                    # StringToVisibility, HexColorToBrush — all registered in App.xaml
├── Core/Enums/                      # ConnectionState, DataFormat, FilterType
├── Helpers/                         # PerformanceMonitor, VersionInfo, BuildInfo.g.cs (GENERATED)
├── Models/                          # SerialPortConfig, LogEntry, FilterRule, CommandPreset, PortStatistics
├── Services/                        # 7 interfaces + 7 implementations (see Service Layer)
├── ViewModels/MainViewModel.cs      # Single ViewModel (+ in-file RangeObservableCollection)
├── scripts/                         # bump-version, generate-buildinfo, generate-release-notes, update-manifest-version
└── .github/workflows/release.yml    # Tag-driven release pipeline
```

`Helpers/BuildInfo.g.cs` is generated on every build (`scripts/generate-buildinfo.ps1`) and is `.gitignore`d — never edit or commit it.

---

## Architecture

### MVVM + Dependency Injection

```
Views (XAML) ←→ ViewModels ←→ Services ←→ Hardware / Infrastructure
```

1. **Dependency injection** — everything is registered in `App.xaml.cs:ConfigureServices()` on a plain `ServiceCollection` (`App.xaml.cs:85-101`):
   - Services are `AddSingleton` (they own shared state, e.g. open ports, regex cache, settings).
   - `MainViewModel` and `MainWindow` are `AddTransient`.
   - Logging is wired through `services.AddLogging(... AddSerilog(dispose: true))`.
2. **Event-driven** — services raise events (`DataReceived`, `PortStateChanged`, `ErrorOccurred`); ViewModels marshal them to the UI thread with `DispatcherQueue`.
3. **Async-first** — all I/O (serial, file writing) is async; nothing blocking runs on the UI thread.

### Service Layer

| Service | Responsibility & key patterns |
| --- | --- |
| `ISerialPortService` / `SerialPortService` | Manages multiple concurrent ports via a concurrent dictionary of `PortInstance`s, each with its own read thread and event handlers. Send/receive, automatic reconnection, integration with baud-rate detection + data validation. |
| `IBaudRateDetectorService` / `BaudRateDetectorService` | Analyses incoming data to detect a wrong baud rate; tracks error rate / pattern consistency; suggests corrections. Called on every reception by `SerialPortService`. |
| `IDataValidationService` / `DataValidationService` | Real-time data-quality assessment (garbage data, encoding issues, quality score). Per-port state (`PortValidationState`) has a **locked** queue (see "do not regress"). |
| `ILogFilterService` / `LogFilterService` | Regex/text/log-level/port filtering. Compiled `Regex` objects cached in a `ConcurrentDictionary` with LRU eviction (max 50, clears half) and a **100 ms match timeout** so a pathological pattern cannot freeze the UI. |
| `IFileLoggerService` / `FileLoggerService` | Async batched file writing: `ConcurrentQueue` + periodic flush (100 ms or 100 items), `StreamWriter` with a 64 KB buffer, background thread, reused `StringBuilder`. |
| `ISettingsService` / `SettingsService` | Persists user preferences / port configs to `%LOCALAPPDATA%\SerialPortTool\settings.json`. Writes are serialized with a **file lock** (see "do not regress"). |
| `ITuningProtocolService` / `TuningProtocolService` | Loads a `TuningProtocolDescriptor` (JSON), packs a `.bin` payload and broadcasts it to one or all open ports. **Each port gets its own send worker** so concurrent multi-port sends do not serialize. Every send pre-checks `IsPortOpen`. |

### ViewModel Layer

`MainViewModel` (~2300 lines) coordinates everything: log collection, filtering, search history, port lifecycle, tuning. Performance-critical details:

- Logs live in `RangeObservableCollection<T>` (declared inside `ViewModels/MainViewModel.cs`), which raises a single `CollectionChanged` for batch operations. Use `AddRange(IEnumerable<T>)`; for FIFO retention use `RemoveFromStart(int)` (v1.8.3) rather than `RemoveRange`.
- Pre-allocate list capacity when batching; never add items one-by-one in a loop.
- Pending updates from background threads are merged before being applied on the UI thread (v1.8.6 flicker fix).

### Performance-Critical Components

1. **`Models/LogEntry.cs`** — caches formatted text in `_cachedFormattedText`; only `Content`, `PortName`, `Timestamp`, `IsReceived` invalidate it (`LogEntry.cs:90-93`). `ColorHex`, `Format`, `RawData` do **not** participate in `FormattedText`.
   **Gotcha**: if you add a field that belongs in `FormattedText`, add its own `partial void OnXxxChanged(...) => _cachedFormattedText = null;` — otherwise the UI silently keeps showing stale text.
2. **`RangeObservableCollection`** — batch add/remove with one notification; `AddRange` / `RemoveFromStart` are the fast paths.
3. **Regex caching** — see `LogFilterService` above (5–10× faster than recompiling).
4. **Batched file writing** — see `FileLoggerService` above (10–20× faster than synchronous writes).
5. **`Controls/LogListView.xaml`** — a `ListView` wrapped in a `UserControl`. The wrapper exists because a WinUI 3 `Window` is not a `FrameworkElement`; hosting the list in a `UserControl` lets the `DataTemplate` use compiled `x:Bind` (~5–10× faster per item than reflection `{Binding}`) — see the comment at `LogListView.xaml:10-13`.
   - `ItemsStackPanel CacheLength="0.5"` halves off-screen realization.
   - Empty `ItemContainerTransitions` + a minimal `Normal`/`Selected`-only visual-state template (kills per-item layout invalidation).
   - `FormattedText`/`ColorHex` are set once at construction and bound `OneTime`, so no per-item `PropertyChanged` wiring.
   - Selection: `Ctrl+C` copies, `Ctrl+A` selects all, right-click opens a `MenuFlyout` (`LogListView.xaml:24-29`).

### Log Buffer Trim Thresholds (`ViewModels/MainViewModel.cs:696-708`)

Collections intentionally overshoot before trimming — trimming on every overflow caused flicker up to v1.8.6:

```csharp
private const int MaxDisplayLogs          = 2000;                     // steady-state size of DisplayLogs
private const int DisplayLogTrimThreshold = MaxDisplayLogs + 200;     // trim only above 2200
private const int AllLogsTrimThreshold    = MaxDisplayLogs * 2 + 400; // 4400 — unfiltered back-buffer
private const int MaxQueuedLogEntries     = MaxDisplayLogs * 4;       // 8000 — pending-update queue cap
private const int StatsRefreshIntervalMs  = 250;                      // stats refresh throttle (~4 Hz)
```

Raising `MaxDisplayLogs` without raising the thresholds reintroduces the overflow→trim→overflow flicker.

### Version Management

`version.json` is the **single source of truth** (version + changelog). The flow:

1. MSBuild reads `version.json` at evaluation time with a regex → `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion` (`SerialPortTool.csproj:18-34`).
2. `UpdateManifestVersion` target runs `scripts/update-manifest-version.ps1` before build to sync the `Package.appxmanifest` `Identity` version.
3. `GenerateBuildInfo` target runs `scripts/generate-buildinfo.ps1` before compile → `Helpers/BuildInfo.g.cs` (UTC build timestamp, surfaced via `Helpers/VersionInfo.cs` in the About dialog).

**Bump the version** by editing `version.json` only, or:
```powershell
.\scripts\bump-version.ps1 -BumpType patch
```

> **Script gotcha (fixed, do not reintroduce):** `scripts/update-manifest-version.ps1` must use `${1}`/`${2}` group references, **never** `$1`/`$2`. A version starting with a digit made .NET parse `$11.7.0.0` as the non-existent group `$11`, which silently replaced the whole `<Identity …/>` element with the literal `$11.7.0.0" />` (broken from v1.7.0 until it was repaired). The script now warns and no-ops when no `Identity` match is found.

---

## Key Code Patterns

### Adding a service

1. Interface `Services/I<Name>.cs`, implementation `Services/<Name>.cs`.
2. Register in `App.xaml.cs:ConfigureServices()` — choose the lifetime that matches the consumer:
   ```csharp
   services.AddSingleton<IYourService, YourService>(); // shared state
   services.AddTransient<YourViewModel>();             // view models / windows
   ```
3. Inject via constructor.

### Handling serial-port events

Services raise events on background threads; always marshal to the UI thread:

```csharp
_serialPortService.DataReceived += async (sender, e) =>
{
    await DispatcherQueue.EnqueueAsync(() =>
    {
        DisplayLogs.AddRange(batch); // UI-bound collections only on the UI thread
    });
};
```

### Batch collection updates

```csharp
var newLogs = new List<LogEntry>(capacity: estimatedSize);
// … populate …
DisplayLogs.AddRange(newLogs); // one notification — never add in a loop
```

### Regex filtering

```csharp
if (_logFilterService.ShouldDisplay(entry)) { … } // cached compiled regex
if (Regex.IsMatch(text, pattern)) { … }           // avoid: compiles every call
```

### File logging

```csharp
await _fileLoggerService.LogAsync(entry);          // batched, background thread
File.AppendAllText(path, entry.ToString());        // never: blocks the UI thread
```

---

## Common Development Scenarios

**New log filter type** — add the enum value in `Core/Enums/FilterType.cs`, handle it in `LogFilterService.ShouldDisplay()`, add UI in the filter panel if needed, and keep any expensive operation cached.

**Baud-rate detection patterns** — edit `BaudRateDetectorService.AnalyzeDataQuality()`; adjust confidence thresholds in `SuggestBaudRate()`; validate against real device data at multiple baud rates.

**New custom control** — follow the `UserControl`-wrapping-a-`ListView` pattern in `Controls/LogListView.xaml` so compiled `x:Bind` keeps working.

**New converter** — add it next to the existing ones in `Converters/` and register it in `App.xaml` `Application.Resources`.

---

## Tuning / TOTA

- `TuningProtocolService` sends a `.bin` payload described by a JSON `TuningProtocolDescriptor`. `mic-tota-tuning.json` at the repo root is the **sample** for that descriptor format (it is *not* application configuration).
- The main window's Tuning panel selects the `.bin` and the JSON descriptor; sends broadcast to **all open ports**, each on its own send worker.
- Every send checks `IsPortOpen` first — auto-send can fire while a port is mid-reconnect.
- If you change the descriptor format, update `mic-tota-tuning.json`, this section, and the README feature blurb in the same change.

---

## CI/CD and Release

**Workflow**: `.github/workflows/release.yml`, triggered by tags matching `v*`.

Build job: validate `version.json` against the git tag → generate `BuildInfo.g.cs` → restore/build for x64 Release → self-contained publish (no R2R / single-file / trimming) → package a ZIP (strip `*.pdb`, keep only `zh-CN` + `en-us` framework language folders) → upload artifact.
Release job: generate release notes from the `version.json` changelog → create or update the GitHub Release with the ZIP assets.

**To release**:
1. Update `version.json` (version + changelog) — or run `.\scripts\bump-version.ps1 -BumpType patch`, which also commits and tags.
2. `git push` the commit, then `git push origin v<version>`.
3. GitHub Actions builds and publishes the release.

---

## Important Constraints

- **Platform**: x64 Windows only (ARM64 support removed in v1.4.0).
- **Minimum Windows version**: 10.0.17763 (Windows 10 1809).
- **Publish settings**: `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` — required for WinUI 3 stability.
- **Language**: C# 12 (required by `CommunityToolkit.Mvvm` partial properties).
- **Packaging**: unpackaged (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`).

---

## Debugging

- **Application logs**: `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log` (daily rolling, 7-day retention, 50 MB cap per file — configured in `App.xaml.cs:38-52`). "工具 → 打开日志文件夹" opens the folder.
- **Log level**: `Information` (`App.xaml.cs:44`).
- **Global exception handlers** (`App.xaml.cs:57-68`): `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are logged via Serilog. Do not remove them — the app used to terminate silently on background-thread exceptions.
- **Profiling**: `Helpers/PerformanceMonitor.cs`
  ```csharp
  using (_perfMonitor.Measure("OperationName")) { … }
  _perfMonitor.LogReport();
  ```
  Operations slower than 100 ms are warned about automatically.

---

## Reliability Mechanisms (do not regress)

Each of these exists because a specific bug caused a crash or an error storm; removing them looks like simplification right up to the next incident.

- **Settings file lock** (`SettingsService`, v1.8.10) — serializes writes to `settings.json` so the tuning watcher and a port-open path cannot corrupt it.
- **Reconnect cooldown** (`SerialPortService`, v1.8.10) — a failed reconnect enforces backoff; without it a yanked port produces thousands of `UnauthorizedAccessException`.
- **Port reopen retry** (`SerialPortService` / `PortInstance.Dispose`, v1.8.11) — on close, `_isClosing` is reset, availability is re-checked while the OS releases the COM handle, and the cleanup delay lives in `Dispose`'s `finally` so it also runs on exception paths.
- **Validation queue lock** (`DataValidationService`, v1.8.10) — the per-port queue is locked because it was mutated concurrently by the read thread and the validation worker.
- **Tuning send pre-check** (`TuningProtocolService`, v1.8.10) — `IsPortOpen` is checked before every send, required because auto-send can fire mid-reconnect and caused `CancellationTokenSource` disposal crashes.
- **Shutdown timeout** (`App.xaml.cs:114-160`) — window-close cleanup runs on a thread-pool task with a hard 5-second wall clock; on timeout the app force-exits instead of hanging on a stuck COM handle.

---

## Notable User-Facing Features (architectural)

Only the ones that change how you should reason about the code — `version.json` has the full changelog.

- **Multi-port management** — open/close individual ports, "open all"/"close all", "scan ports".
- **Per-port colours** (v1.7.0) — each opened port gets a unique colour from a 10-colour palette stored on `LogEntry.ColorHex`, plus a configurable TX colour. Surfaced through `HexColorToBrushConverter` in `LogListView.xaml`'s `DataTemplate`. New `LogEntry` fields needing colour treatment must go through the same converter.
- **Log pause toggle** — pauses UI appending without stopping reception; buffering continues while paused and batched updates resume on unpause. Anything touching the data-flow pipeline must respect this.
- **Search history** (v1.5.0 / v1.6.2) — debounced persistence, per-item delete, clear-all with confirmation; visibility driven by `StringToVisibilityConverter`.
- **Baud-rate mismatch banner** — surfaced when detection confidence is high, with one-click correction.
- **Tuning broadcast** — see the Tuning/TOTA section.

---

## Data Flow: Receiving Serial Data

```
Hardware serial port (background thread)
  ↓
SerialPortService.DataReceivedHandler (validates / detects baud rate)
  ↓
DataReceived event raised (still background thread)
  ↓
MainViewModel event handler
  ↓
DispatcherQueue.EnqueueAsync (switch to UI thread)
  ↓
LogFilterService.ShouldDisplay (cached regex)
  ↓
RangeObservableCollection.AddRange (batched UI update)
  ↓
Controls/LogListView.xaml (virtualized ListView, x:Bind)
  ↓
FileLoggerService.LogAsync (async batched write to disk)
```

---

## Known Issues and Limitations

- **Port close reliability** — Windows can hold a COM handle after close. Handled by retry + cleanup-delay in `finally`; read "Reliability Mechanisms" before touching `SerialPortService.PortInstance.Dispose`.
- **Very high baud rates** (>921600) — some data loss is possible; consider larger buffers in `SerialPortService`.
- **Complex regex** — heavy backtracking can hit the 100 ms timeout. Keep patterns simple for real-time filtering.

---

## Documentation Map

| File | Audience | Update when |
| --- | --- | --- |
| `README.md` | Users / contributors | Features, requirements, build/run, tech stack, docs policy |
| `AGENTS.md` (this file) | Developers / AI agents | Anything architectural, procedural, or "how to work here" |
| `CLAUDE.md` | Claude Code | Only the pointer to this file |
| `version.json` | Release tooling / changelog | Every shipping change |
| `.github/workflows/release.yml` | CI | Release/publish process |
