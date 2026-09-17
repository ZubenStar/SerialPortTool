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

- WinUI 3 (Windows App SDK `1.6.241114003`, self-contained), C# 13 (the .NET 9 SDK default; `LangVersion` is not pinned), .NET 9
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

# Package the publish output into a single Setup.exe (requires Inno Setup 6 → packages/installer)
.\scripts\build-installer.ps1
```

**Prerequisites**: .NET 9 SDK, Windows App SDK 1.6 runtime/build tools, Windows 10 1809+ (Windows 11 recommended). The build invokes PowerShell scripts, so it must run on Windows. `scripts/build-installer.ps1` additionally needs **Inno Setup 6** (`ISCC.exe`); it fails with an explicit message when it cannot find the compiler.

### Application Icon
`Assets/Images/logo.ico` **must stay multi-resolution**: it currently carries 16, 20, 24, 28, 32, 40, 48, 56, 64, 96 and 128 px as BMP/DIB entries plus 256 px as a PNG entry. Explorer, the taskbar, the title bar and the small-icon views each request a different frame; an ico holding a single (or only large) frame gets resampled and shows up blurry in the shell — this regressed once and must not happen again. Frames ≤ 24 px are intentionally a bolder, hole-free variant of the symbol, because the connector's pin holes are sub-pixel at that size. `logo.png` is the 1024 px master kept for documentation/branding; the app never loads it.

### Testing
There is **no automated test suite**. Verification is manual:

- open several ports simultaneously (open/close/reopen, including with a wrong baud rate first),
- send/receive at various baud rates, including hex mode,
- exercise log filtering (plain text + regex) under a high-throughput stream,
- run a tuning broadcast across ≥2 ports,
- update path: "Help → Check for updates" against a Release that has a higher version, then a full download → silent replace → auto-restart against an installed older build (see the Update System section),
- update failure paths: offline, request timeout, and a Release without a `Setup` asset.

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
├── Assets/Images/                   # logo.ico (multi-size 16–256), logo.png (1024 master)
├── Controls/LogListView.xaml(.cs)   # The only custom UserControl (virtualized log list)
├── Converters/                      # BoolToVisibility + InverseBoolToVisibility (one file),
│                                    # HexColorToBrush — all registered in App.xaml
├── Core/Enums/                      # ConnectionState, DataFormat, FilterType, UpdateCheckStatus
├── Helpers/                         # VersionInfo, BuildInfo.g.cs (GENERATED)
├── Models/                          # SerialPortConfig, LogEntry, FilterRule, CommandPreset, PortStatistics,
│                                    # UpdateReleaseInfo / UpdateCheckResult
├── Services/                        # 9 interfaces + 9 implementations (see Service Layer)
├── ViewModels/MainViewModel.cs      # Single ViewModel (+ in-file RangeObservableCollection)
├── installer/SerialPortTool.iss     # Inno Setup script — per-user install, silent replace/restart on update
├── scripts/                         # bump-version, generate-buildinfo, generate-release-notes,
│                                    # update-manifest-version, build-installer
└── .github/workflows/release.yml    # Tag-driven release pipeline
```

`Helpers/BuildInfo.g.cs` is generated on every build (`scripts/generate-buildinfo.ps1`) and is `.gitignore`d — never edit or commit it.

---

## Architecture

### MVVM + Dependency Injection

```
Views (XAML) ←→ ViewModels ←→ Services ←→ Hardware / Infrastructure
```

1. **Dependency injection** — everything is registered in `App.xaml.cs:ConfigureServices()` on a plain `ServiceCollection` (`App.xaml.cs:85-103`):
   - Services are `AddSingleton` (they own shared state, e.g. open ports, regex cache, settings).
   - `MainViewModel` and `MainWindow` are `AddTransient`.
   - Logging is wired through `services.AddLogging(... AddSerilog(dispose: true))`.
   - `MainWindow` has a parameterless constructor (required by XAML), so it resolves its dependencies with `App.Current.Services.GetRequiredService<T>()` instead of constructor injection. Follow that pattern when a window needs a new service; do not add constructor parameters to `MainWindow`.
2. **Event-driven** — services raise events (`DataReceived`, `PortStateChanged`, `ErrorOccurred`); ViewModels marshal them to the UI thread with `DispatcherQueue`.
3. **Async-first** — all I/O (serial, file writing) is async; nothing blocking runs on the UI thread.

### Service Layer

| Service | Responsibility & key patterns |
| --- | --- |
| `ISerialPortService` / `SerialPortService` | Manages multiple concurrent ports via a concurrent dictionary of `PortInstance`s, each with its own read thread and event handlers. Send/receive, automatic reconnection, integration with baud-rate detection + data validation. Each port's `SerialPort_DataReceived` validates **inline and forwards before reading the next chunk** (single-threaded per port); a garbage verdict arms a ~1 s drop cooldown. |
| `IBaudRateDetectorService` / `BaudRateDetectorService` | Analyses incoming data to detect a wrong baud rate; tracks error rate / pattern consistency; suggests corrections. Called on every reception by `SerialPortService`. |
| `IDataValidationService` / `DataValidationService` | Real-time data-quality assessment (garbage data, encoding issues, quality score). `ValidateDataAsync` runs **synchronously on the caller's (read) thread** — it is not `Task.Run`-wrapped, so per-port statistics stay ordered. Binary / non-ASCII payloads skip the lossy `CleanData` and pass through unchanged. Per-port state (`PortValidationState`) has a **locked** queue (see "do not regress"). |
| `ILogFilterService` / `LogFilterService` | Regex/text/log-level/port filtering. Compiled `Regex` objects cached in a `ConcurrentDictionary` with LRU eviction (max 50, clears half) and a **100 ms match timeout** so a pathological pattern cannot freeze the UI. |
| `IFileLoggerService` / `FileLoggerService` | Async batched file writing: `ConcurrentQueue` + periodic flush (100 ms or 100 items), `StreamWriter` with a 64 KB buffer, background thread, reused `StringBuilder`. Hot paths hand a whole batch to `WriteLogs(portName, entries)`; `WriteLogAsync` is for a single entry. |
| `ISettingsService` / `SettingsService` | Persists user preferences / port configs to `%LOCALAPPDATA%\SerialPortTool\settings.json`. Writes are serialized with a **file lock** (see "do not regress"). |
| `ITuningProtocolService` / `TuningProtocolService` | Loads a `TuningProtocolDescriptor` (JSON), packs a `.bin` payload and broadcasts it to one or all open ports. **Each port gets its own send worker** so concurrent multi-port sends do not serialize. Every send pre-checks `IsPortOpen`. |
| `IUpdateService` / `UpdateService` | Checks `api.github.com/repos/ZubenStar/SerialPortTool/releases/latest` for a newer version, compares versions **numerically** via `Version.TryParse`, and owns the 24 h silent-check cache + "skip this version" policy. Static `HttpClient` (needs `User-Agent` + `Accept: application/vnd.github+json`), ~10 s timeout, returns a result object instead of throwing. `IsInstalledBuild` delegates to `InstalledBuildInfo`. |
| `IUpdateInstallerService` / `UpdateInstallerService` | Streams the `Setup*.exe` Release asset into `%TEMP%\SerialPortTool\Update`, verifies it (length vs. asset `size`, non-empty, `MZ` PE header), then launches it as the **external updater process** with `/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`. `InstalledBuildInfo` (same file) gates auto-replacement on the exe living under `%LOCALAPPDATA%\Programs\SerialPortTool` **and** the Inno Setup uninstall key existing. |

### ViewModel Layer

`MainViewModel` (~2300 lines) coordinates everything: log collection, filtering, search history, port lifecycle, tuning. Performance-critical details:

- Logs live in `RangeObservableCollection<T>` (declared inside `ViewModels/MainViewModel.cs`), which raises a single `CollectionChanged` for batch operations. Use `AddRange(IEnumerable<T>)`; for FIFO retention use `RemoveFromStart(int)` (v1.8.3) rather than `RemoveRange`.
- Pre-allocate list capacity when batching; never add items one-by-one in a loop.
- Pending updates from background threads are merged before being applied on the UI thread (v1.8.6 flicker fix).
- Serial chunks are reassembled **per port** before splitting: `PortLineAssembler` (a persistent `Decoder` + `StringBuilder`) carries UTF-8 sequences and partial lines across `DataReceived` events, and `ExtractCompleteLines` emits only terminator-delimited lines (`\n`, `\r`, `\r\n`), leaving a trailing `\r` buffered for the next chunk.

### Performance-Critical Components

1. **`Models/LogEntry.cs`** — caches formatted text in `_cachedFormattedText`; only `Content`, `PortName`, `Timestamp`, `IsReceived` invalidate it (`LogEntry.cs:85-88`). `ColorHex`, `Format`, `RawData` do **not** participate in `FormattedText`.
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

## Update System

Two singleton services plus one build-time artifact. Deliberately dependency-free — no auto-updater library, no `Newtonsoft.Json`.

### Update check (`UpdateService`)

- Endpoint: `https://api.github.com/repos/ZubenStar/SerialPortTool/releases/latest`. GitHub requires a `User-Agent` header (a missing one returns 403); `Accept: application/vnd.github+json` is sent too.
- Version comparison is **numeric** (`Version.TryParse` on `tag_name` stripped of a leading `v`). String comparison would rank `1.8.10` below `1.8.9`.
- Throttling is a hard requirement, not an optimization: anonymous GitHub API calls are capped at 60/hour, so silent checks are cached for 24 h via `Update.LastCheckUtc`.
- Silent checks must never surface errors — failures log at `Debug` and return a `Failed` result the caller ignores. Never log the raw response body.
- Settings keys (all **strings**, because `ISettingsService` only has `int`/`string` overloads — do not extend that interface for this):
  - `Update.LastCheckUtc` — ISO 8601 round-trip timestamp.
  - `Update.SkippedVersion` — the version the user chose to skip (silent checks stop nagging; manual checks still report it).
- The installer asset is picked from `assets[]` as the `.exe` whose name contains `Setup`. The portable ZIP is intentionally never used for auto-update.

### Update install (`UpdateInstallerService`)

- Downloads to `%TEMP%\SerialPortTool\Update` with `HttpCompletionOption.ResponseHeadersRead` (streaming, so memory use is independent of package size) and reports throttled progress through `IProgress<double>`.
- **Verification is mandatory before launching**: non-empty, length equal to the asset `size` (when known), and an `MZ` PE header. Without it a 403/HTML error page would be executed as an installer.
- The downloaded installer *is* the external updater process, launched with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NOCANCEL`. `CloseApplications=yes` + `RestartApplications=yes` in `installer/SerialPortTool.iss` perform "close the app → replace files → restart it". That is why no bespoke updater executable exists: a hand-rolled one would add antivirus false positives, elevation problems, and half-written app directories.
- Auto-replacement is gated on `InstalledBuildInfo.IsInstalled()`: the running exe must live under `%LOCALAPPDATA%\Programs\SerialPortTool` **and** the Inno Setup uninstall key must exist. A portable ZIP build therefore only gets a notification plus the download page — it must never silently overwrite a user-chosen folder.
- Exit reuses the existing shutdown path: flush settings (`ISettingsService.FlushAsync()`) → `LaunchInstaller()` → `MainWindow.Close()` → `App.OnWindowClosed` (5 s hard timeout, `Environment.Exit(0)`). Do not introduce a second exit mechanism.
- `IProgress<double>` callbacks are produced off the UI thread, so they are marshalled back with `DispatcherQueue.TryEnqueue` before touching the `ProgressBar`.

### Installer (`installer/SerialPortTool.iss` + `scripts/build-installer.ps1`)

- `PrivilegesRequired=lowest` → per-user install into `%LOCALAPPDATA%\Programs\SerialPortTool`, no administrator rights and no UAC prompt. That is what makes a fully silent update possible; moving to `Program Files` would trigger a UAC prompt on every update.
- The **`AppId` GUID is permanent**. It must stay identical to `InstalledBuildInfo.AppId` (`Services/UpdateInstallerService.cs`); changing either turns every upgrade into a side-by-side install and orphans the previous one in "Apps & features".
- `AppVersion` is injected on the command line by `scripts/build-installer.ps1` (sourced from `version.json`) — never hard-code a version inside the `.iss`.
- `[Files]` `Excludes` is injected by the build script as `*.pdb` **plus every framework language folder except `zh-CN` and `en-us`**. A self-contained publish carries ~86 language folders (168 `.mui` files — 37 % of the file count) that no zh/en user will ever load; the portable ZIP already dropped them, so the installer must too. The list is computed by scanning the actual publish output, so an unrelated new folder is never dropped by accident — only language folders are ever excluded.
- `[Files]` must **not** carry `createallsubdirs`. Inno Setup skips empty directories by default, but that flag also creates directories that became empty *because of* `Excludes`, which leaves 84 empty `xx-YY` folders in the install directory and defeats the whole point of the exclusion. Measured on a real install: with the flag → 93 directories, 84 of them empty; without it → 9 directories, all populated (`Assets`, `Assets\Images`, `en-us`, `Microsoft.UI.Xaml`, `Microsoft.UI.Xaml\Assets`, `runtimes`, `runtimes\win-x64`, `runtimes\win-x64\native`, `zh-CN`). Verified install footprint: 289 files / 169.5 MB (287 payload + `unins000.exe` + `unins000.dat`).
- `[InstallDelete]` clears `{app}\*` before installing so assemblies and language resources dropped by a newer version do not linger. User settings (`%LOCALAPPDATA%\SerialPortTool`) and logs (`Documents\SerialPortTool`) live outside `{app}` and are preserved by design — uninstall must not delete them either.
- Compression is `lzma2/ultra64` + `SolidCompression`. The self-contained payload is several hundred MB, so the in-app download must keep its progress bar and cancel button.
- Inno Setup does not bundle a Simplified Chinese language file. `build-installer.ps1` detects `Languages\ChineseSimplified.isl` next to `ISCC.exe` and only then passes `/DIncludeChinese=1`, so the installer still compiles on a stock Inno Setup install (English UI).
- The installer does not change the publish settings. `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` remain required for WinUI 3 stability; the fix for "too many files in the install directory" is packaging, not trimming.

---

## CI/CD and Release

**Workflow**: `.github/workflows/release.yml`, triggered by tags matching `v*`.

Build job: validate `version.json` against the git tag → generate `BuildInfo.g.cs` → restore/build for x64 Release → self-contained publish (no R2R / single-file / trimming) → **install Inno Setup (chocolatey) and run `scripts/build-installer.ps1 -SkipPublish`** → package a portable ZIP (strip `*.pdb`, keep only `zh-CN` + `en-us` framework language folders) → upload the ZIP **and the Setup.exe** as artifacts.
Release job: generate release notes from the `version.json` changelog → create or update the GitHub Release with **both** the ZIP and the Setup.exe.

> `scripts/build-installer.ps1` publishes to `publish/x64` itself when run without `-SkipPublish`, so the workflow reuses the already-published output and never produces a second, differently-configured build.

**To release**:
1. Update `version.json` (version + changelog) — or run `.\scripts\bump-version.ps1 -BumpType patch`, which also commits and tags.
2. `git push` the commit, then `git push origin v<version>`.
3. GitHub Actions builds and publishes the release.

---

## Important Constraints

- **Platform**: x64 Windows only (ARM64 support removed in v1.4.0).
- **Minimum Windows version**: 10.0.17763 (Windows 10 1809).
- **Publish settings**: `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` — required for WinUI 3 stability.
- **Language**: C# 13 (the .NET 9 SDK default; `LangVersion` is not pinned). `[ObservableProperty]` is applied to backing fields, not partial properties.
- **Packaging**: unpackaged (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), distributed either as a per-user Inno Setup installer or as a portable ZIP.
- **Auto-update scope**: silent file replacement runs **only** for the installed build (`InstalledBuildInfo.IsInstalled()`). The portable ZIP must never be replaced in place — it only shows a notification and opens the download page.
- **No update framework**: the update feature uses only `System.Net.Http` + `System.Text.Json` from the BCL. `Newtonsoft.Json` stays removed (v1.8.5); do not reintroduce it for this path.
- **Installer identity**: the `AppId` GUID is shared between `installer/SerialPortTool.iss` and `InstalledBuildInfo.AppId`. They must stay in sync, and the GUID must never change after a release.

---

## Debugging

- **Application logs**: `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log` (daily rolling, 7-day retention, 50 MB cap per file — configured in `App.xaml.cs:38-52`). "工具 → 打开日志文件夹" opens the folder.
- **Log level**: `Information` (`App.xaml.cs:44`).
- **Global exception handlers** (`App.xaml.cs:57-68`): `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are logged via Serilog. Do not remove them — the app used to terminate silently on background-thread exceptions.

---

## Reliability Mechanisms (do not regress)

Each of these exists because a specific bug caused a crash or an error storm; removing them looks like simplification right up to the next incident.

- **Settings file lock** (`SettingsService`, v1.8.10) — serializes writes to `settings.json` so the tuning watcher and a port-open path cannot corrupt it.
- **Reconnect cooldown** (`SerialPortService`, v1.8.10) — a failed reconnect enforces backoff; without it a yanked port produces thousands of `UnauthorizedAccessException`.
- **Port reopen retry** (`SerialPortService` / `PortInstance.Dispose`, v1.8.11) — on close, `_isClosing` is reset, availability is re-checked while the OS releases the COM handle, and the cleanup delay lives in `Dispose`'s `finally` so it also runs on exception paths.
- **Validation queue lock** (`DataValidationService`, v1.8.10) — the per-port `PortValidationState` queue stays locked. Validation now runs inline on the read thread, but `ResetValidationState` can still be called from the UI thread.
- **Tuning send pre-check** (`TuningProtocolService`, v1.8.10) — `IsPortOpen` is checked before every send, required because auto-send can fire mid-reconnect and caused `CancellationTokenSource` disposal crashes.
- **Single-threaded per-port decode/validation** (`SerialPortService`, v1.8.13) — `SerialPort_DataReceived` awaits validation before reading the next chunk, and a garbage verdict arms a ~1 s drop cooldown, so the per-port `Decoder`/`StringBuilder` is only ever touched by one thread. Do not make validation fire-and-forget again.
- **Shutdown timeout** (`App.xaml.cs:116-162`) — window-close cleanup runs on a thread-pool task with a hard 5-second wall clock; on timeout the app force-exits instead of hanging on a stuck COM handle.
- **Installer verification before launch** (`UpdateInstallerService`, v2.0.0) — the downloaded `Setup*.exe` is rejected unless it is non-empty, matches the Release asset `size`, and starts with `MZ`. Launching an unverified download would execute a 403/HTML error page as an installer on a bad network.
- **Installed-build gate** (`UpdateInstallerService.InstalledBuildInfo`, v2.0.0) — auto-replacement requires both the `%LOCALAPPDATA%\Programs\SerialPortTool` location **and** the Inno Setup uninstall key. Removing the gate would let the app silently overwrite a user's portable folder.
- **Silent-check log level** (`UpdateService`, v2.0.0) — failures of the automatic check log at `Debug` only, and silent checks are cached for 24 h. Raising this produces an error storm whenever the machine is offline, and dropping the cache burns through GitHub's 60/hour anonymous limit.
- **Flush-before-handoff** (`MainWindow.DownloadAndInstallAsync`, v2.0.0) — `ISettingsService.FlushAsync()` completes before the installer is launched. Skipping it loses the last ~500 ms of debounced settings (including `Update.SkippedVersion`) on every auto-update.
- **Installer / ZIP language-list parity** (`scripts/build-installer.ps1` + `.github/workflows/release.yml`, v2.0.0) — both artifacts keep only the `zh-CN` and `en-us` framework language folders, and the `[Files]` entry must stay free of `createallsubdirs` so the excluded folders do not reappear as empty directories. This degrades silently rather than crashing: drop either half and ISCC still compiles without a warning — the installer just packs 168 extra `.mui` files and/or recreates 84 empty `xx-YY` folders. The kept-language list is duplicated (Inno Setup cannot read the workflow file), so change both sides together. A `Compressing:` line count from the ISCC log catches the first half; only a real install catches the second.

---

## Notable User-Facing Features (architectural)

Only the ones that change how you should reason about the code — `version.json` has the full changelog.

- **Multi-port management** — open/close individual ports, "open all"/"close all", "scan ports".
- **Per-port colours** (v1.7.0) — each opened port gets a unique colour from a 10-colour palette stored on `LogEntry.ColorHex`, plus a configurable TX colour. Surfaced through `HexColorToBrushConverter` in `LogListView.xaml`'s `DataTemplate`. New `LogEntry` fields needing colour treatment must go through the same converter.
- **Log pause toggle** — pauses UI appending without stopping reception; buffering continues while paused and batched updates resume on unpause. Anything touching the data-flow pipeline must respect this.
- **Search history** (v1.5.0 / v1.6.2) — debounced persistence, per-item delete, clear-all with confirmation.
- **Baud-rate mismatch banner** — surfaced when detection confidence is high, with one-click correction.
- **Tuning broadcast** — see the Tuning/TOTA section.
- **Check for updates / auto-update** (v2.0.0) — "Help → Check for updates" for a manual check, plus a silent check a few seconds after the window is first activated. Finding a newer version opens a `ContentDialog` with the release notes and the release page. `ContentDialog` only has Primary / Secondary / Close slots, so the buttons are apportioned per scenario: installed + silent → "Download and install / Skip this version / Later"; installed + manual → "Download and install / Open download page / Close"; portable (cannot self-install) → "Open download page / Skip this version (silent only) / Later". Dialogs are built in code-behind following the `About_Click` pattern, with `XamlRoot = Content.XamlRoot` and a re-entrancy guard because WinUI 3 cannot show two `ContentDialog`s at once.

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
FileLoggerService.WriteLogs (async batched write to disk)
```

---

## Known Issues and Limitations

- **Port close reliability** — Windows can hold a COM handle after close. Handled by retry + cleanup-delay in `finally`; read "Reliability Mechanisms" before touching `SerialPortService.PortInstance.Dispose`.
- **Very high baud rates** (>921600) — some data loss is possible; consider larger buffers in `SerialPortService`.
- **Complex regex** — heavy backtracking can hit the 100 ms timeout. Keep patterns simple for real-time filtering.
- **Installer language** — Inno Setup ships no Simplified Chinese language file, so the installer wizard falls back to English unless `Languages\ChineseSimplified.isl` is present next to `ISCC.exe`. The application itself is unaffected.
- **Update download size** — a self-contained WinUI 3 payload is several hundred MB, so a full auto-update is a large download even with `lzma2/ultra64`. Progress and cancel must stay functional.

---

## Documentation Map

| File | Audience | Update when |
| --- | --- | --- |
| `README.md` | Users / contributors | Features, requirements, build/run, tech stack, docs policy |
| `AGENTS.md` (this file) | Developers / AI agents | Anything architectural, procedural, or "how to work here" |
| `CLAUDE.md` | Claude Code | Only the pointer to this file |
| `version.json` | Release tooling / changelog | Every shipping change |
| `.github/workflows/release.yml` | CI | Release/publish process |
| `installer/SerialPortTool.iss` + `scripts/build-installer.ps1` | Release tooling | Anything about installation, the `AppId`, install location, or the update hand-off |
