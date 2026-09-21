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
| The tuning protocol descriptor format, or the hidden Tuning feature switch | the Tuning / TOTA section below (there is no bundled sample descriptor) |

Hard rules:

1. **Never** leave a documented path, type, method, constant, or version stale. If you rename/move/delete something referenced in a doc, fix the reference in the same commit.
2. Sections marked **"do not regress"** describe mechanisms that exist because a specific bug caused a crash or a storm. Do not remove or "simplify" them without an explicit, documented reason.
3. **Do not add new top-level `*.md` files.** Extend `README.md` (user-facing) or `AGENTS.md` (developer/agent-facing) instead. If a plan/spec document is needed for a single task, do not commit it.
4. If a change cannot be verified by `dotnet build`, say so explicitly in the summary.

---

## Project Overview

**SerialPortTool (串口工具)** is a Windows desktop serial-port debugging / monitoring tool built with WinUI 3 and .NET 9. It focuses on multi-port simultaneous monitoring, real-time log filtering, baud-rate mismatch detection, and (behind an opt-in hidden switch) pushing tuning/TOTA firmware payloads over the wire.

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

Three consumers read that file, and they do **not** refresh the same way:

| Consumer | Source of the icon | How it picks up a new icon |
| --- | --- | --- |
| Window title bar / taskbar | `AppWindow.SetIcon({app}\Assets\Images\logo.ico)` at startup (`MainWindow.xaml.cs:77-81`) | every launch — always current |
| `SerialPortTool.exe` inside Explorer | the Win32 icon resource embedded by `<ApplicationIcon>` | shell icon cache |
| Desktop / Start-menu shortcut | `[Icons] IconFilename` → `{app}\Assets\Images\logo.ico` | shell icon cache + an Explorer repaint |

The bottom two are cached by Windows (per-size `iconcache_*.db` **and** Explorer's in-memory system image list) keyed by source path. A silent auto-update overwrites the same paths and the app exits immediately, so nothing tells Explorer to re-read them — the shortcut keeps showing the previous release's artwork until the user presses F5 or restarts Explorer. That is what the installer's `SHChangeNotify` call exists for (see the Installer section and "do not regress"); bumping the ico to a new name is **not** a fix, the cache key is the path, not the content.

### Testing
There is **no automated test suite**. Verification is manual:

- open several ports simultaneously (open/close/reopen, including with a wrong baud rate first),
- send/receive at various baud rates, including hex mode,
- exercise log filtering (plain text + regex) under a high-throughput stream. With the commit-based search this means: typing filters **nothing** until Enter / 搜索 (stream data while typing a pattern and watch the view stay put), 文本 mode — the default — matches `(` `[` `*` literally and never reports an error, flipping `.*` on makes the same query report 正则表达式无效：…, flipping `Aa` changes the match set, both switches survive a restart, the 历史 flyout only opens on a click, a committed query is what gets recorded there, and per-item delete plus 清空历史 (with confirmation) both work,
- tuning is hidden by default: on a fresh profile the toolbar must show **no** Tuning row and 工具 → 启用 Tuning 功能 must start unticked. Tick it, select a `.bin` + descriptor, and broadcast across ≥2 ports; then untick it while the watch is running — the panel must disappear, the watch must stop, and re-ticking must bring the panel back and resume the watch (the `.bin` / JSON paths and the was-watching preference survive the untick),
- appearance: switch 跟随系统 / 浅色 / 深色 from the 外观 menu and check legibility of the log rows, the channel legend, the baud-rate banner, the regex error, the title-bar caption buttons and the dialogs; then restart and confirm the choice survived. Do this with ≥2 ports open so the per-port colours are actually exercised,
- shell: fold/unfold the rail, resize across the 900 px breakpoint, and maximise/restore to confirm content is not hidden under the caption buttons,
- update path: "Help → Check for updates" against a Release that has a higher version, then a full download → silent replace → auto-restart against an installed older build (see the Update System section),
- update failure paths: offline, request timeout, and a Release without a `Setup` asset.

Added in v2.1.5 — the log list's wheel step (a ~20 px row made the framework's own step invisible; see the `LogListView` entry under Performance-Critical Components):

- **one notch is a real move**: with a few hundred to a few thousand rows on screen, one wheel notch must move the view by roughly a fifth of the list height (60–240 px), never by a single row. Repeat up the whole list and back down.
- **no stacking**: the amount must not be larger than that — if it feels like it jumps about twice the intended distance, the framework's own step is no longer being suppressed (the primary registration is not running before the ScrollViewer's class handler), which is the one thing to re-verify after a Windows App SDK upgrade.
- **proportional for a touchpad**: a precision wheel / two-finger flick sends small `MouseWheelDelta` values; motion must be smooth and proportional, not a full step per event.
- **held down stays responsive**: hold the wheel (or flick repeatedly) through a stream of ~1000 lines/s — the offset must follow, not lag behind or snap back.
- **ends are clean**: scrolling into the top and the bottom must stop there without a hitch or an overshoot, and the scrollbar thumb must track the view.
- **paused and live**: pause the view and scroll freely; unpause — the wheel still works, and the very next batch of arriving lines pulls the view back to the newest row (unchanged "locked to latest" behaviour).
- **nothing else regressed**: `Ctrl+C` / `Ctrl+A` / right-click copy still work on a scrolled-to position, the selection is not disturbed by wheel scrolling, and switching 浅色/深色 or folding the rail does not break the wheel.

Added in v2.1.2 — the silent-check cadence was reworked (see the Update System section):

- **check on every launch**: launch the build twice in a row. Each launch must hit the network — count requests with `GET https://api.github.com/rate_limit` (reading it does not consume quota). With a Release newer than the running build, the app log shows `Update available: …` on every launch, not just the first.
- **no success-side cache**: `Update.LastCheckUtc` must be gone from `settings.json` after the first launch of this build, and must not come back.
- **failure backoff**: go offline and launch twice. The first launch logs the failure at `Debug` and writes `Update.SilentFailureUtc`; the second launch within the hour must skip without any request and without changing the timestamp. Restore the network, and confirm a later success deletes the key.
- **manual check is side-effect free**: with a failure timestamp present, Help → Check for updates must hit the network immediately and leave `Update.SilentFailureUtc` untouched.
- **runtime check**: temporarily lower `RuntimeUpdateCheckInterval` to ~1 minute and leave the app open; the repeated requests must appear in the `rate_limit` accounting, with no dialog while up to date. Restore 24 h afterwards — this is the only way to exercise a 24 h timer.
- **shutdown with the timer armed**: close the window while the runtime timer is running — the process must still exit immediately, and no update dialog may be raised after the window is gone.

Added in v2.1.3 — "closing a port needs the whole app to be closed" came back twice from the same shape (an in-flight auto-reconnect, and a running baud-rate scan). Both are reproduced by opening a port **at a wrong baud rate** and waiting for the Frame-error storm:

- **close during a reconnect**: with `AutoReconnect` on (the default) and a mismatched baud rate, the app reconnects every few seconds. Click 关闭 again and again until one click lands inside a reconnect — the port must stay closed, 打开 must work immediately, and the app log must show the reconnect being abandoned (`Reconnect of COMx was abandoned…` / `Refusing to open COMx…`), never an `opened successfully` after the close. Repeat ~20 times; a single resurrection is the bug.
- **close during a baud-rate scan**: let the quality watchdog start a scan (app log: `Starting baud rate detection for port COMx`), then close the port. The scan must be cancelled, the port must be released immediately (it must be openable from another program), and no `Successfully opened … with baud rate …` may follow the close.
- **close all during a scan**: same setup, but press 全部关闭. The port must be gone from the system, not "closed" with `_ports` empty while the detector still holds the handle.
- **no zombie ports**: after any of the above, 扫描串口 → 打开 the same port again must succeed without restarting the app. `Error closing port … but it was not in the open ports collection` in the log is the smell of an untracked handle, not a harmless warning.
- **close-all during an open**: with ≥2 ports open, hit 打开 on a third and immediately press 全部关闭. No port may appear afterwards; the log must show `finished opening after a close-all request; closing it again`. Repeat with 全部打开 followed immediately by 全部关闭.
- **last close always wins**: click 关闭 on port A and port B back to back (the close button is a `Click` handler, not a command binding, so both run concurrently) — both must end up closed, with their loggers stopped and their rows gone.
- **port name casing**: open a port, then close it through a path that carries a differently-cased spelling (e.g. a `settings.json` written by hand as `com7`, or `CloseAllPortsAsync` vs a row whose `PortName` was entered lowercase). The close must still find the instance — a default-comparer dictionary here turns the close into a silent no-op.
- **shutdown during an open**: start 全部打开 on a machine with several ports and close the window mid-batch. No `Port … opened successfully` may be followed by anything but a close, the process must exit within ~1 s (5 s worst case), and no port may be left open.

Added in v2.1.1 — each of these is a regression that was actually reported or reproduced:

- **shutdown**: open 3 ports, then close the window while data is streaming. No `ObjectDisposedException` in the log, the process is gone within a second, and the ports reopen afterwards (repeat open/close/reopen on the same port 10 times).
- **single instance**: launch a second copy while the first is running — one message box, no second window, a `Warning` in the app log, and `settings.json` unchanged. On a two-user or multi-session machine, confirm the second *user* is not blocked.
- **settings safety**: truncate `settings.json` (or replace its contents with `{`), start the app, and confirm the file is byte-identical afterwards, the status bar reports the read-only state, and port configs/colours survive once the file is repaired.
- **log cap**: stream past 2000 lines and keep wheel-scrolling up — the view must settle instead of being rebuilt ~20 times per second, and the log must oscillate rather than sit pinned at the cap.
- **sent logs**: pause the view, send a few hundred frames, unpause — `AllLogs`/`DisplayLogs` must not have grown without bound, and sent lines must respect an active search filter.
- **clipboard**: hold the clipboard open in another process (or copy repeatedly in a browser) and press Ctrl+C — the copy fails with a status message, the app stays alive.
- **send box**: Enter sends; nothing is sent twice; the button is disabled while a send is in flight.
- **hex input**: `0x0A 0x0B` and `A0-0B` both send the expected bytes; `A0x0B` is rejected as an odd-length payload rather than silently mangled.
- **custom baud rate**: `0` and `999999999` are rejected with a message; a valid value still opens.
- **port colour**: with several ports open and history on screen, change one port's colour — every row of that port (and the legend and swatch) changes together, sent rows keep the TX colour.
- **baud-rate detection**: trigger a detection and close the window mid-scan — the process exits promptly and the port is left closed, not half-reopened.
- **tuning**: start auto-send, then keep writing the `.bin` continuously — sends are skipped with "仍在写入" rather than sending a partial file; once writing stops, the next change sends normally.

---

## Repository Layout

```
SerialPortTool/
├── App.xaml / App.xaml.cs           # Entry point: Serilog, DI container, global exception handlers, shutdown
├── MainWindow.xaml / .cs            # Main window — lives at the ROOT (there is no Views/ folder)
├── Package.appxmanifest             # Kept for MSIX tooling; unused assets referenced (see note above)
├── SerialPortTool.csproj / .sln
├── version.json                     # Single source of truth: version + changelog
├── Assets/Images/                   # logo.ico (multi-size 16–256), logo.png (1024 master)
├── Themes/Tokens.xaml               # Design tokens + WinUI lightweight-styling overrides (Light/Dark/HC)
├── Themes/Controls.xaml             # Button family styles/templates (the only re-templated controls)
├── Controls/LogListView.xaml(.cs)   # The only custom UserControl (virtualized log list)
├── Converters/                      # BoolToVisibility + InverseBoolToVisibility (one file),
│                                    # HexColorToBrush — all registered in App.xaml
├── Core/Enums/                      # ConnectionState, FilterType, UpdateCheckStatus, AppThemePreference
├── Helpers/                         # VersionInfo, BuildInfo.g.cs (GENERATED)
├── Models/                          # SerialPortConfig, LogEntry, FilterRule, PortStatistics,
│                                    # PortColorSlot / PortColorPalette (port identity palette),
│                                    # UpdateReleaseInfo / UpdateCheckResult
├── Services/                        # 9 interfaces + 9 implementations (see Service Layer)
├── ViewModels/MainViewModel.cs      # Single ViewModel (+ in-file RangeObservableCollection,
│                                    # PortViewModel, PortColorOption)
├── installer/SerialPortTool.iss     # Inno Setup script — per-user install, silent replace/restart on update
├── scripts/                         # bump-version, generate-buildinfo, update-manifest-version,
│                                    # build-installer, prune-publish-output
└── .github/workflows/release.yml    # Tag-driven release pipeline
```

`Helpers/BuildInfo.g.cs` is generated on every build (`scripts/generate-buildinfo.ps1`) and is `.gitignore`d — never edit or commit it.

---

## Architecture

### MVVM + Dependency Injection

```
Views (XAML) ←→ ViewModels ←→ Services ←→ Hardware / Infrastructure
```

1. **Dependency injection** — everything is registered in `App.xaml.cs:ConfigureServices()` on a plain `ServiceCollection` (`App.xaml.cs:119-137`):
   - Services are `AddSingleton` (they own shared state, e.g. open ports, regex cache, settings).
   - `MainViewModel` and `MainWindow` are `AddTransient`.
   - Logging is wired through `services.AddLogging(... AddSerilog(dispose: true))`.
   - `MainWindow` has a parameterless constructor (required by XAML), so it resolves its dependencies with `App.Current.Services.GetRequiredService<T>()` instead of constructor injection. Follow that pattern when a window needs a new service; do not add constructor parameters to `MainWindow`.
2. **Event-driven** — services raise events (`DataReceived`, `PortStateChanged`, `ErrorOccurred`); ViewModels marshal them to the UI thread with `DispatcherQueue`.
3. **Async-first** — all I/O (serial, file writing) is async; nothing blocking runs on the UI thread.

### Service Layer

| Service | Responsibility & key patterns |
| --- | --- |
| `ISerialPortService` / `SerialPortService` | Manages multiple concurrent ports via a concurrent dictionary of `PortInstance`s, each with its own read thread and event handlers. Send/receive, automatic reconnection, integration with data validation. Each port's `SerialPort_DataReceived` validates **inline and forwards before reading the next chunk** (single-threaded per port); a garbage verdict arms a ~1 s drop cooldown. Closing a port is **owned** by the service: `ClosePortAsync` removes the instance and calls `PortInstance.RequestTeardown()` *before* closing, which is what makes a user close win over an in-flight auto-reconnect (see "do not regress"). Implements **`IAsyncDisposable`** — the container is torn down through `ServiceProvider.DisposeAsync()`, so real closing happens there; `Dispose()` is a synchronous best-effort fallback with no `Task.Run(...).Wait(timeout)`. |
| `IBaudRateDetectorService` / `BaudRateDetectorService` | Probes candidate baud rates by **opening the port itself** for a short listen window and scoring the printable-character ratio. It is **not** on the receive path: `SerialPortService` does not depend on it at all (that constructor parameter was dead and has been removed). The flow is driven from `MainViewModel.OnBaudRateDetectionRequested` → close the port → `DetectOptimalBaudRateAsync` → reopen, and it runs **outside** `SerialPortService`'s bookkeeping — which is why the ViewModel tracks it per port (`_baudRateDetections`) and the close paths cancel it. Both public methods take a `CancellationToken`; the whole scan is ~40 s (18 rates × test window) and is cancelled on window close. |
| `IDataValidationService` / `DataValidationService` | Real-time data-quality assessment (garbage data, encoding issues, quality score). `ValidateDataAsync` runs **synchronously on the caller's (read) thread** — it is not `Task.Run`-wrapped, so per-port statistics stay ordered. Binary / non-ASCII payloads skip the lossy `CleanData` and pass through unchanged. Per-port state (`PortValidationState`) has a **locked** queue (see "do not regress"). Hot-path helpers (`ComputePrintableRatio`, `AnalyzeCharacterDistribution`, `IsGarbageData`) are allocation-free span/bitmap scans — do not reintroduce LINQ there. |
| `ILogFilterService` / `LogFilterService` | Regex/text/log-level/port filtering. Compiled `Regex` objects cached in a `ConcurrentDictionary` with a **100 ms match timeout** so a pathological pattern cannot freeze the UI. Eviction is *arbitrary*, not LRU: once the cache reaches 50 entries, half of the current keys are dropped (`ConcurrentDictionary` has no ordering to exploit). Currently unused by the UI — the live search filter is `MainViewModel`'s own cached regex — but it is registered and its `FiltersChanged` event has no subscribers yet. |
| `IFileLoggerService` / `FileLoggerService` | Async batched file writing: `ConcurrentQueue` + periodic flush (100 ms or 100 items), `StreamWriter` with a 64 KB buffer, background thread, reused `StringBuilder`. Hot paths hand a whole batch to `WriteLogs(portName, entries)`; `WriteLogAsync` is for a single entry. Start/Stop are serialized by a service-wide `_lifecycleLock` (see "do not regress"); `LoggerInstance.DisposeAsync` is idempotent. |
| `ISettingsService` / `SettingsService` | Persists user preferences / port configs to `%LOCALAPPDATA%\SerialPortTool\settings.json`. Writes are serialized with a **file lock** and are **atomic** (same-directory temp file + `File.Move(overwrite: true)`). A file that exists but cannot be read/parsed puts the service into a **read-only protection state** for the rest of the session — see "do not regress". Raises `SettingsLoadFailed` once, which `MainViewModel` surfaces in the status bar. |
| `ITuningProtocolService` / `TuningProtocolService` | Loads a `TuningProtocolDescriptor` (JSON — comments and trailing commas are allowed), packs a `.bin` payload into TOTA packet frames. It does **not** send: `MainViewModel.SendTuningFileAsync` owns the broadcast (one send worker per port, delay plan, `IsPortOpen` pre-check, baseline-hash bookkeeping). |
| `IUpdateService` / `UpdateService` | Checks `api.github.com/repos/ZubenStar/SerialPortTool/releases/latest` for a newer version, compares versions **numerically** via `Version.TryParse`, and owns the silent-check **failure backoff** (1 h) + "skip this version" policy. Silent checks are **not** throttled after a success (v2.1.2): every launch checks, so a freshly published release can no longer hide behind a cache. Static `HttpClient` (needs `User-Agent` + `Accept: application/vnd.github+json`), ~10 s timeout, returns a result object instead of throwing. `IsInstalledBuild` delegates to `InstalledBuildInfo`. |
| `IUpdateInstallerService` / `UpdateInstallerService` | Streams the `Setup*.exe` Release asset into `%TEMP%\SerialPortTool\Update`, verifies it (length vs. asset `size`, non-empty, `MZ` PE header), then launches it as the **external updater process** with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL` (never `/RESTARTAPPLICATIONS` — see the Update System section). `InstalledBuildInfo` (same file) gates auto-replacement on the exe living under `%LOCALAPPDATA%\Programs\SerialPortTool` **and** the Inno Setup uninstall key existing. |

### ViewModel Layer

`MainViewModel` (~3000 lines) coordinates everything: log collection, filtering, search history, port lifecycle, tuning. Performance-critical details:

- Logs live in `RangeObservableCollection<T>` (declared inside `ViewModels/MainViewModel.cs`), which raises a single `CollectionChanged` for batch operations. Use `AddRange(IEnumerable<T>)`; for FIFO retention use `RemoveFromStart(int)` (v1.8.3) rather than `RemoveRange`.
- Pre-allocate list capacity when batching; never add items one-by-one in a loop.
- Pending updates from background threads are merged before being applied on the UI thread (v1.8.6 flicker fix).
- `_portsByName` is a **`ConcurrentDictionary`**, written on the UI thread from `OpenPorts.CollectionChanged` and read from each port's read thread in `GetPortColor` / `GetPortDisplayColor`. Keep it concurrent — a plain `Dictionary` here is a data race on the hottest path in the app.
- **Every** log entry takes the same route: received lines are queued by `OnDataReceived`, and locally generated entries (TX, tuning summaries) go through `AddSentLog` → the same `_pendingLogBatches` queue → `FlushPendingLogBatches`. Never write to `AllLogs`/`DisplayLogs` directly: that bypasses the FIFO trim, the search filter, the batch window and the queued-count cap.
- Serial chunks are reassembled **per port** before splitting: `PortLineAssembler` (a persistent `Decoder` + `StringBuilder`) carries UTF-8 sequences and partial lines across `DataReceived` events, and `ExtractCompleteLines` emits only terminator-delimited lines (`\n`, `\r`, `\r\n`), leaving a trailing `\r` buffered for the next chunk.
- Search is split into a **draft and an applied query**: `SearchDraft` is bound to the box and filters nothing, `SearchText` is the committed query and is the only input to `FilterLogs()` and to the flush loop (both through `GetOrCreateSearchMatcher`). Committing is `ExecuteSearchCommand` (Enter / 搜索). Binding the box straight to `SearchText` would make uncommitted keystrokes filter live traffic, because `FlushPendingLogBatches` snapshots `SearchText` to decide whether a newly arrived line is displayed.
- Re-filtering has exactly one entry point: `RequestFilterLogs()` (a 150 ms debounce on a reused `System.Threading.Timer`). `FilterLogs()` itself is private. Do not call a full rebuild synchronously from a UI handler — three of them used to, which cancelled out the debounce entirely.
- Long-running background flows take `_shutdownCts.Token` (cancelled in `Dispose`) and marshal UI updates with `RunOnUiThread`. Do not pass an `async` lambda to `DispatcherQueue.TryEnqueue`: it is a de-facto `async void` whose exceptions land in the XAML unhandled-exception handler and whose continuations outlive the window.
- The baud-rate scan is tracked per port in `_baudRateDetections` (a `ConcurrentDictionary<string, CancellationTokenSource>`, one entry per running scan, removed by the scan itself in its `finally`). It closes the port for its whole duration and reopens it at the end, entirely outside `SerialPortService`, so **every** close path (`ClosePortAsync`, `CloseAllPortsAsync`) calls `CancelBaudRateDetection(portName)` first and `ReopenPortWithBaudRateAsync` refuses to reopen a port that left `_portsByName`. Do not drop either half: without the cancellation the close is undone a moment later; without the reopen guard the port comes back as a handle nothing can reach.

### Performance-Critical Components

1. **`Models/LogEntry.cs`** — caches formatted text in `_cachedFormattedText`; only `Content`, `PortName`, `Timestamp`, `IsReceived` invalidate it (`LogEntry.cs:90-93`). `ColorHex`, `Format`, `RawData` do **not** participate in `FormattedText`.
   **Gotcha**: if you add a field that belongs in `FormattedText`, add its own `partial void OnXxxChanged(...) => _cachedFormattedText = null;` — otherwise the UI silently keeps showing stale text.
2. **`RangeObservableCollection`** — batch add/remove with one notification; `AddRange` / `RemoveFromStart` are the fast paths.
3. **Regex caching** — see `LogFilterService` above (5–10× faster than recompiling).
4. **Batched file writing** — see `FileLoggerService` above (10–20× faster than synchronous writes).
5. **`Controls/LogListView.xaml`** — a `ListView` wrapped in a `UserControl`. The wrapper exists because a WinUI 3 `Window` is not a `FrameworkElement`; hosting the list in a `UserControl` lets the `DataTemplate` use compiled `x:Bind` (~5–10× faster per item than reflection `{Binding}`) — see the comment at `LogListView.xaml:10-13`.
   - `ItemsStackPanel CacheLength="0.5"` halves off-screen realization.
   - Empty `ItemContainerTransitions` + a minimal `Normal`/`Selected`-only visual-state template (kills per-item layout invalidation).
   - Exactly **two elements per row** — the channel-colour `Border` and the text `TextBlock`. Column alignment is carried by `LogEntry.FormattedText` rather than by extra columns, because each additional container is paid for on every realize. Adding hover states, transitions or wrappers here is a measured decision, not a cosmetic one.
   - `FormattedText` stays `OneTime` (it never changes after construction). `ColorHex` is deliberately `OneWay`, because an appearance switch re-colours rows that are already on screen; the extra `PropertyChanged` wiring is attached only to realized containers, which is bounded by the viewport, not by list length.
   - Selection: `Ctrl+C` copies, `Ctrl+A` selects all, right-click opens a `MenuFlyout` (`LogListView.xaml:24-29`). `SelectAll()` / `CopySelection()` are the public entry points the toolbar uses; `CopySelection()` returns `false` when nothing is selected so the caller can say so.
   - **Wheel scrolling is taken over by the control** (v2.1.5). Rows here are ~20 px tall (item `MinHeight`/`Padding`/`Margin` are all 0, one `NoWrap` 13 px line), so the framework's per-notch step moved the view by barely a row and read as "the wheel does nothing" — it was a step-size problem, not a throughput one (it reproduced at a few dozen lines/s, and identically while paused). `LogListView.xaml.cs` resolves the template's `ScrollViewer` on `Loaded` and attaches one `PointerWheelChanged` handler to it in two places: the ScrollViewer's **content** (the items presenter) with `handledEventsToo: false`, so it runs *before* the ScrollViewer's own class handler and its `e.Handled = true` stops that step from being added on top, plus the `ListView` itself with `handledEventsToo: true` for gestures that never travel through the items presenter (the empty area below the last row hit-tests the ScrollViewer directly — that second registration is a no-op whenever the first one ran, because of the `if (e.Handled) return;` guard). The step is `Clamp(ViewportHeight × 0.2, 60, 240)` px scaled by `MouseWheelDelta / 120` (a precision wheel or touchpad sends fractional deltas and must stay proportional), applied with `ChangeView(..., disableAnimation: true)` so it cannot race the auto-follow `ScrollIntoView`. `HorizontalMouseWheel` is deliberately left to the framework. Do not move the primary registration to the `ListView` alone (it would then run after the ScrollViewer and the two steps would stack), do not drop the `Unloaded` detach (`HookWheelInterception` runs on every `Loaded` and a double attach doubles the step), and do not change the row height without re-checking `MinWheelStepPixels` (≈ 3 rows).

### Log Buffer Trim Thresholds (`ViewModels/MainViewModel.cs`)

Collections intentionally overshoot before trimming — trimming on every overflow caused flicker up to v1.8.6:

```csharp
private const int MaxDisplayLogs          = 2000;                        // cap on DisplayLogs
private const int DisplayLogTrimThreshold = MaxDisplayLogs + 200;        // trim only above 2200
private const int DisplayLogTrimHeadroom  = 400;                         // trim down to 1600, not to 2000
private const int AllLogsTrimThreshold    = MaxDisplayLogs * 2 + 400;    // 4400 — unfiltered back-buffer
private const int MaxQueuedLogEntries     = MaxDisplayLogs * 4;          // 8000 — pending-update queue cap
private const int StatsRefreshIntervalMs  = 250;                         // stats refresh throttle (~4 Hz)
```

Raising `MaxDisplayLogs` without raising the thresholds reintroduces the overflow→trim→overflow flicker.

`DisplayLogTrimHeadroom` is the second half of that fix. `RemoveFromStart` publishes a `Reset`, and a `Reset` discards every realized ListView container — so trimming *down to* the cap meant a full rebuild on every 50 ms flush once the buffer was full (~20 Hz, forever). Trimming to `MaxDisplayLogs - DisplayLogTrimHeadroom` puts the next trim ~600 entries away. The list therefore oscillates between 1600 and 2200 rather than sitting pinned at the cap. A multi-item `Remove` notification would preserve the scroll anchor and is the better fix, but WinUI 3's vector view is known to mishandle multi-item collection notifications (that is why multi-item `Add` was abandoned) and the failure is an uncatchable exception inside the ListView's own handler — do not switch without verifying it on this framework version first.

### Version Management

`version.json` is the **single source of truth** (version + changelog). The flow:

1. MSBuild reads `version.json` at evaluation time → `Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion` (`SerialPortTool.csproj`). The regex is **anchored to the opening brace** (`(?m)^\s*\{\s*"version"\s*:\s*"([^"]+)"`), so it can only match the top-level key. A bare `"version"\s*:\s*"…"` matches the first occurrence *anywhere*, which becomes a changelog entry the moment the top-level key is reordered — and the build then silently stamps the wrong version on the assembly, the About dialog and the installer. There is **no hard-coded fallback**: it used to be `1.7.0` while the project was on 2.1.0.
2. The `ValidateVersion` target (`BeforeTargets="BeforeBuild"`) fails the build when the version is missing or not three numeric parts. Nothing downstream (manifest sync, compile, installer) ever sees an empty version.
3. `UpdateManifestVersion` target runs `scripts/update-manifest-version.ps1` before build to sync the `Package.appxmanifest` `Identity` version. The script fails (non-zero exit) on a missing file, a missing `Identity` element, or a post-condition mismatch — it used to warn and `exit 0`, which MSBuild reports as success while the manifest keeps the previous release's version.
4. `GenerateBuildInfo` target runs `scripts/generate-buildinfo.ps1` before compile → `Helpers/BuildInfo.g.cs` (UTC build timestamp, surfaced via `Helpers/VersionInfo.cs` in the About dialog).

**Bump the version** by editing `version.json` only, or:
```powershell
.\scripts\bump-version.ps1 -BumpType patch
```

> **Script gotcha (fixed, do not reintroduce):** `scripts/update-manifest-version.ps1` must use `${1}`/`${3}` group references, **never** `$1`/`$3`. A version starting with a digit made .NET parse `$11.7.0.0` as the non-existent group `$11`, which silently replaced the whole `<Identity …/>` element with the literal `$11.7.0.0" />` (broken from v1.7.0 until it was repaired). The script now **fails the build** when it finds no `Identity` match or when the resulting attribute is not the requested version.

> **Script encoding convention.** Every script writes text through `[System.IO.File]::WriteAllText` with an explicit `UTF8Encoding($false)` (BOM-less), **except** `update-manifest-version.ps1`, which must keep the UTF-8 **BOM** for `Package.appxmanifest` (the MSIX tooling expects it). `Set-Content -Encoding UTF8` is the trap: Windows PowerShell 5.1 and PowerShell 7 write different bytes for the same literal, so the same script produced a whole-file diff depending on which shell ran it. `bump-version.ps1`, `generate-buildinfo.ps1`, `update-manifest-version.ps1` and `prune-publish-output.ps1` are the scripts that touch files.

> **Removed, do not restore:** `scripts/generate-release-notes.ps1`. The release job generates the notes inline (`.github/workflows/release.yml`), and the orphaned script had drifted to an old format that only mentioned the ZIP.

---

## UI and Appearance

The interface is a bench-instrument faceplate: graphite / paper neutrals, hairline dividers, monospace data, a single accent that means "live signal". The log is the only element that grows.

### Where the design lives

| File | Owns |
| --- | --- |
| `Themes/Tokens.xaml` | Colour, typography and geometry tokens, **plus overrides of WinUI's own lightweight-styling keys** (`ApplicationPageBackgroundThemeBrush`, `LayerFillColor*`, `Card*`, `DividerStrokeColorDefaultBrush`, `TextFillColor*`, `Control*`, `TextControl*`, `ComboBox*`, `CheckBox*`, `MenuFlyout*`, `ToolTip*`, `ScrollBar*`, `ListViewItem*`, `ContentDialog*`, `ControlCornerRadius`, `OverlayCornerRadius`) |
| `Themes/Controls.xaml` | The button family only — `AppButtonStyle` (the base), `AppToolbarButtonStyle`, `AppIconButtonStyle` (base), `AppToolbarIconButtonStyle`, `AppHistoryItemButtonStyle` (a search-history row), `AccentButtonStyle` (overrides the framework key, so `ContentDialog` primary buttons match the toolbar), `AppToolbarToggleButtonStyle` (the search mode switches; `Checked` fills with the accent) — plus `AppVerticalDividerStyle`. Nothing else is re-templated. |
| `MainWindow.xaml` | Layout. **No colour literals** — only `{ThemeResource App*}` / `{StaticResource App*}` |

Both dictionaries are merged in `App.xaml` **after** `XamlControlsResources`. A merged dictionary only wins if it is consulted after the framework's, so reordering them silently reverts the whole app to the stock palette with no error.

**The override strategy is deliberate.** Re-templating a control whose template carries behaviour would mean re-implementing that behaviour: `ComboBox` backs the baud-rate and port pickers, and `ScrollBar` / `ListViewItem` carry the virtualization contract. Those are themed through lightweight keys instead, which is why `Themes/Controls.xaml` is small. Anything *not* in the two tables above has no business defining a colour.

**High contrast is not an afterthought.** The `HighContrast` theme dictionary maps every token onto `SystemColor*`, and the port palette collapses to the system text colour there (contrast beats hue). A token added to Light and Dark must be added to HighContrast in the same edit.

### Applying the appearance

1. `App.OnLaunched` (`async void`) reads the `AppTheme` setting through `ISettingsService` into `App.InitialThemePreference` **before** the window is resolved from DI. Do not move this read into `MainWindow` — see the "do not regress" entry about the first frame.
2. The `MainWindow` constructor assigns `RootLayout.RequestedTheme` (`ElementTheme.Default` / `Light` / `Dark`) and then samples `RootLayout.ActualTheme` — never the preference — because "follow the system" only resolves to light or dark once the element has actually been themed. `RootLayout.ActualThemeChanged` re-runs the same path, which is also how a live Windows theme switch is picked up while on 跟随系统.
3. `MainViewModel.ApplyEffectiveTheme(bool isDark)` re-derives everything stored as a palette **slot** and refreshes `TxColorOptions`; log rows already on screen are re-coloured by walking `AllLogs` in place (bounded by `AllLogsTrimThreshold`).
4. `MainWindow.ApplyTitleBarColors` assigns the caption-button colours by hand: those buttons are composited outside the XAML tree, so nothing in `Tokens.xaml` reaches them.

`Application.RequestedTheme` is intentionally **not** used — it is immutable after startup.

### The port colour model: slots, not rendered values

`Models/PortColorSlot.cs` defines ten slots, each with a light hex and a dark variant. **Only the light hex (`SlotHex`) is ever persisted** — `PortColor_<port>`, `TxColorHex`, `RxColorHex`, and the `Tag` of the port-colour menu items. It is the slot's identity. Rendering always goes through `PortColorPalette.Resolve(hex, isDark)`, which also accepts an already-resolved hex so it is idempotent; an unknown hex (hand-edited settings) passes through unchanged. Old settings files therefore need no migration.

Two consequences worth remembering:

- `PortViewModel.ColorHex` is the slot, `PortViewModel.DisplayColorHex` is what the swatch binds to. The same split exists on `MainViewModel` between `TxColorHex` (persisted) and the private `TxColorHexResolved` (rendered, and what is written into `LogEntry.ColorHex`).
- The ten swatches of the port-colour `MenuFlyout` are static XAML, so the palette exists a second time as `AppPortColor1Brush`…`AppPortColor10Brush` in `Tokens.xaml`. **Change both together** — a mismatch is invisible in review and obvious on screen.

### Window shell

- `MainWindow.SetupTitleBar()` enables `ExtendsContentIntoTitleBar` + `SetTitleBar(AppTitleBar)` only when `AppWindowTitleBar.IsCustomizationSupported()` is true (Windows 11). On Windows 10 the caption buttons cannot be re-coloured and interactive content inside the drag region is not supported, so the system title bar is kept and `TitleBarInsetSpacer` stays at zero width — `AppTitleBar` then simply reads as the app's own header band. Do not force extension on for Windows 10.
- `TitleBarInsetSpacer.Width` and the title bar's left padding are recomputed from `TitleBar.RightInset` / `LeftInset` on every `AppWindow.Changed` carrying `DidSizeChange`, because those metrics follow DPI and window state rather than being fixed.
- `SetupBackdrop()` applies `MicaBackdrop` only when `MicaController.IsSupported()` and clears the root background only then; otherwise the XAML opaque brush stays. See the "do not regress" entry.
- The rail folds either because the user asked (`SidebarCollapsed`, persisted) or because the window is narrower than 900 px (not persisted, so widening the window restores the user's choice). `SidebarHost.Width` animates over 150 ms through a `DoubleAnimation` with `EnableDependentAnimation` (width drives layout, so the compositor cannot run it), and animation is skipped entirely when `UISettings.AnimationsEnabled` is false.
- Every `ContentDialog` goes through `MainWindow.CreateDialog()`. A dialog lives in its own popup root and does **not** inherit the window root's `ElementTheme`; without the explicit `RequestedTheme` assignment they stay light in dark mode.

### Keyboard shortcuts

Window-level gestures are resolved in **one** place: `MainWindow.OnRootKeyDown`, reached through a `KeyDown` handler added to the root content `Grid` (`RootLayout`) with `handledEventsToo: true`. That element spans the whole window, so a gesture works no matter which control has focus — and `handledEventsToo` matters, because the search and send boxes mark their own editing keys as handled.

**Do not switch this back to `KeyboardAccelerator` (v2.2.0).** A `KeyboardAccelerator` was tried first, declared on that same `RootLayout`, and never fired in this application: `Ctrl+Shift+L` and `Ctrl+Alt+L` both did nothing at all, while the same keys work in other programs. Two more strikes against it here: `Alt` is the menu-activation key, which the `MenuBar` is entitled to consume first, and a declared accelerator advertises itself in its owner's tooltip by default since Windows 10 1803 — on an element spanning the whole window that produced a `Ctrl+Alt+L` tooltip under the pointer everywhere in the UI. `MainWindow` is a `Window`, not a `FrameworkElement`, so accelerators cannot live on the window itself either.

| Gesture | Resolved in | Handler |
| --- | --- | --- |
| `F9` — open the log folder | `MainWindow.OnRootKeyDown` (root Grid `KeyDown`) | `OpenLogFolder()` (shared with the menu's `OpenLogFolder_Click`) |

The 工具 → 打开日志文件夹 menu item carries its own `KeyboardAccelerator Key="F9"` for **display**: a `MenuFlyoutItem` renders the gesture beside the item text (the framework's documented exception to the tooltip behaviour) and keeps working while the menu is open. It cannot double-fire with `OnRootKeyDown` — a flyout is a separate popup root, so its key events never travel through the window's element tree, and a `MenuFlyoutItem` only routes keys while its flyout is open.

Other keyboard gestures in the app are **control-scoped** rather than window-scoped and stay where they are — do not migrate them without a reason:

- `Ctrl+C` / `Ctrl+A` in the log list — `Controls/LogListView.xaml.cs` `InnerListView_KeyDown`, active only while the list has focus.
- `Enter` to search / to send — `MainWindow.SearchBox_KeyDown` and `MainWindow.SendTextBox_KeyDown`.

To add a window-level shortcut: add a `case` to `OnRootKeyDown` and, if it belongs in a menu, the matching `KeyboardAccelerator` on that menu item. The handler switches on `e.Key` alone, so a gesture that needs modifiers must read them explicitly (`InputKeyboardSource.GetKeyStateForCurrentThread`, as `LogListView` does). Prefer a **function key**: `Ctrl+Shift+字母` and similar combinations are routinely claimed by IMEs and resident tools.

### XAML rules that are not stylistic

- **A compiled `{x:Bind}` with a `Converter` cannot be used in the window's own element tree.** The generated code calls `SetConverterLookupRoot(this)`, and a WinUI 3 `Window` is not a `FrameworkElement`, so the build fails with `CS1503` inside `MainWindow.g.cs` — pointing at generated code, not at your XAML. `RootLayout` therefore carries `DataContext="{x:Bind ViewModel}"` and anything needing a converter uses `{Binding}`; plain value bindings still use `{x:Bind}`. Inside a `DataTemplate` the bindings object is element-scoped, which is why `LogListView.xaml` can use converters freely.
- **An XML comment may not contain `--`.** The XAML compiler reports malformed XAML by exiting with code 1 and printing *nothing at all* — the build shows a bare `MSB3073` with no file, no line and no message (this cost real time in v2.1.0). Use `====` for section dividers, never `----`. The same silence applies to any XAML parse error, so when `MSB3073` appears with no diagnostic, check XML well-formedness first.
- **Keep `VisualState.Setters` targets to plain style properties.** `Setter.Target` is a *property path* resolved at runtime, and composition-backed properties cannot be resolved at all:
  `<Setter Target="Presenter.Translation" Value="0,1,0" />` compiles, loads, and then throws `The property path 'Translation' could not be resolved for a Setter` **the first time the state is entered** — so it surfaces as "the app starts fine and dies the moment you hover or click a button", which is very hard to attribute. `Translation`, `Scale`, `Rotation`, `CenterPoint` and `TransformMatrix` are all in that class. Use `Background`, `BorderBrush`, `Opacity`, `Visibility` and similar classic DPs. This is why the button press feedback is a background swap with no 1px sink, and why a `Setter.Value` should be a simple primitive: the value is `object` and converted on state entry, so a wrong type also fails at interaction time rather than at build time.
- Icons are `FontIcon` glyphs with `FontFamily="Segoe Fluent Icons, Segoe MDL2 Assets"`. `Segoe Fluent Icons` ships only with Windows 11 while the minimum platform is Windows 10 1809, so the fallback is mandatory and only code points present in **both** fonts may be used. Text labels accompany icons; a glyph never carries meaning on its own.
- The channel legend and the toolbar chips are `ItemsControl`s over `ViewModel.OpenPorts` / `ViewModel.TxColorOptions`; their `DataTemplate`s set their own `DataContext`, so reflection `{Binding}` there is correct and not a regression.

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

Services raise events on background threads. The receive path does **not** marshal per event — it
queues into `_pendingLogBatches` and lets the 50 ms UI flush apply the batch, which is what keeps a
high-throughput stream from starving the UI thread of input:

```csharp
// OnDataReceived (background): decode → split lines → enqueue
_fileLoggerService.WriteLogs(portName, newLogs);   // disk, batched, background
_pendingLogBatches.Enqueue(new PendingLogBatch { PortName = portName, Logs = newLogs });
SchedulePendingLogFlush();                          // one Low-priority dispatcher work item + 50 ms timer

// FlushPendingLogBatches (UI thread): AllLogs.AddRange → DisplayLogs.AddRange → trim → stats
```

Anything that touches a UI-bound collection must run on the UI thread. Use `RunOnUiThread(Action)` (the
ViewModel) or `DispatcherQueue.TryEnqueue` — and never hand `TryEnqueue` an `async` lambda.

### Batch collection updates

```csharp
var newLogs = new List<LogEntry>(capacity: estimatedSize);
// … populate …
_pendingLogBatches.Enqueue(new PendingLogBatch { PortName = portName, Logs = newLogs });
// NOT: DisplayLogs.AddRange(...) from a background thread, and not one Add per entry either.
```

### Search matching (text + regex)

```csharp
// Cached by (query, mode, case); rebuilt only when one of them changes. UI thread only.
var matcher = GetOrCreateSearchMatcher(SearchText, IsRegexSearch, IsCaseSensitiveSearch);
matcher.IsValid;              // false only for a regex that does not parse
matcher.IsMatch(log.Content); // literal Contains in text mode, Regex.IsMatch in regex mode
Regex.IsMatch(text, pattern); // avoid: compiles on every call and re-derives the mode by hand
```

`FilterLogs()` (rebuild of what is on screen) and `FlushPendingLogBatches()` (newly arrived lines)
must go through the same matcher — they are the "already displayed" and "just arrived" halves of one
predicate, and two separate instances drift on mode/case. `IsValid == false` is only reachable in
regex mode: text mode is always valid, which is what keeps a query like `(` from blanking the log.

`ILogFilterService` holds a separate rule-based cache (`ShouldDisplay`, 100 ms timeout) but nothing
calls it yet — see the Service Layer note.

### File logging

```csharp
_fileLoggerService.WriteLogs(portName, entries);   // whole batch, queued, 64 KB buffered writer
await _fileLoggerService.WriteLogAsync(portName, entry); // single entry
File.AppendAllText(path, entry.ToString());        // never: blocks the UI thread
```

---

## Common Development Scenarios

**New log filter type** — add the enum value in `Core/Enums/FilterType.cs`, handle it in `LogFilterService.MatchesFilter()` / `ShouldDisplay()`, add UI in the filter panel if needed, and keep any expensive operation cached. Note that the live search box is a *separate* mechanism (`MainViewModel.GetOrCreateSearchMatcher` + `FlushPendingLogBatches`), so a new rule type in `LogFilterService` will not appear in the search box's behaviour until the service is actually wired up.

**Baud-rate scoring** — the scoring lives in `BaudRateDetectorService.TestBaudRateAsync` (per-candidate listen window) and `CountValidDataBytes` (printable ratio, with a bonus for `_commonPatternRegex`). Thresholds are `PrintableCharThreshold` (0.7) and `MinDataBytesForValidation` (10). There is no `AnalyzeDataQuality` / `SuggestBaudRate` method — earlier revisions of this file referenced them and they never existed. Validate changes against real device data at multiple baud rates, including the cancel path (close the window mid-scan).

**New custom control** — follow the `UserControl`-wrapping-a-`ListView` pattern in `Controls/LogListView.xaml` so compiled `x:Bind` keeps working.

**New converter** — add it next to the existing ones in `Converters/` and register it in `App.xaml` `Application.Resources`.

---

## Tuning / TOTA

**Hidden feature — off and invisible by default.** The whole feature is gated by one switch, `MainViewModel.IsTuningEnabled`, persisted as the settings key `TuningEnabled` (`int`, `0`/`1`, default `0`):

- The only entry point is the 工具 → 启用 Tuning 功能 `ToggleMenuFlyoutItem`, whose `IsChecked` is two-way bound to `IsTuningEnabled`. There is no other way to reveal the panel.
- The entire second toolbar row (`MainWindow.xaml`, the `Grid Grid.Row="1"` holding the TUNING label, the two file chips, the status text and the 重载 JSON / 开始监听 / 发送 Tuning buttons) binds its `Visibility` to `IsTuningEnabled` through `BoolToVisibilityConverter`. The parent grid row is `Height="Auto"`, so collapsing it leaves the toolbar single-line with no blank strip.
- `RefreshTuningAvailability` puts `IsTuningEnabled` **first**: `CanUseTuning = IsTuningEnabled && IsTuningDescriptorValid && bin path non-empty && File.Exists(bin)`. Every send/watch entry point already guards on `CanUseTuning` (`SendTuningFileAsync`, `StartTuningWatchAsync`, `RestartTuningWatchAsync`), and `BuildTuningUnavailableMessage` reports "Tuning 功能未启用" before any "please pick a .bin" wording — so a hidden panel cannot be sent through, and the message is never misleading.
- **Turning it off stops the watch but keeps the configuration.** `OnIsTuningEnabledChanged` calls `StopTuningWatch(persistState: false)`, which reuses `_suppressTuningWatchPersistence` so the saved `TuningIsWatching` preference survives. `TuningBinFilePath` / `TuningDescriptorFilePath` / `TuningBaselineHash` are never cleared. Re-ticking the menu item calls `ResumeTuningWatchIfPreferredAsync`, which resumes the watch only when the stored preference is `1` and `CanUseTuning` is true ("was watching" ⇒ resumes).
- Startup reads the switch through `InitializeTuningEnabled` (suppression flag `_skipTuningEnabledPersistence`, same shape as the theme's `_skipThemePersistence`): no write-back, no status message, no resume. The resume in `InitializeAsync` is explicitly `IsTuningEnabled && shouldResumeTuningWatch && CanUseTuning`, so a disabled feature can never start a `FileSystemWatcher`.

**Descriptor format.** There is deliberately **no sample descriptor file in the repository** — users supply their own JSON. The format is documented by `TuningProtocolService` + the notes below:

- `TuningProtocolService` **builds** the frames for a `.bin` payload described by a JSON `TuningProtocolDescriptor`. It does not send them: `MainViewModel.SendTuningFileAsync` owns the broadcast (one send worker per port, the delay plan, the `IsPortOpen` pre-check and the baseline-hash bookkeeping).
- The parser (`JsonOptions`) sets `PropertyNameCaseInsensitive`, `ReadCommentHandling = Skip` and `AllowTrailingCommas`, so a descriptor may carry `//` comments and trailing commas and is meant to be hand-edited. Unknown properties are ignored, so a commented-out alternative (like a `packetFrame.layout` block) is safe.
- Validation happens at **load** time (`ValidateDescriptor` → `ValidateField`) and covers `dspMessage.layout`, `tota.headerLayout`, `tota.packetFrame.layout` **and the object form of `tota.infoAreaFields`**. Expression evaluation is `checked`: a length arithmetic overflow and a checksum accumulation overflow both throw `TuningProtocolException` rather than producing a frame whose declared total does not match its bytes.
- The main window's Tuning panel selects the `.bin` and the JSON descriptor; sends broadcast to **all open ports**, each on its own send worker. Auto-send waits for the `.bin` to stop changing (`WaitForStableTuningFileAsync` returns `false` after ~5 s and the send is skipped) — hashing a file mid-write stored a baseline for content that no longer existed, which then suppressed every later auto-send as "unchanged".
- If you change the descriptor format or the switch, update this section and the README feature blurb in the same change.

---

## Update System

Two singleton services plus one build-time artifact. Deliberately dependency-free — no auto-updater library, no `Newtonsoft.Json`.

### Update check (`UpdateService`)

- Endpoint: `https://api.github.com/repos/ZubenStar/SerialPortTool/releases/latest`. GitHub requires a `User-Agent` header (a missing one returns 403); `Accept: application/vnd.github+json` is sent too.
- Version comparison is **numeric** (`Version.TryParse` on `tag_name` stripped of a leading `v`). String comparison would rank `1.8.10` below `1.8.9`.
- **Throttling is inverted from the pre-v2.1.2 behaviour, and that is deliberate**: a *successful* silent check leaves no trace, so every launch checks once. The old "cached for 24 h after the last **successful** check" rule meant a Release published minutes after that check stayed invisible for up to a day — reproduced against v2.0.4, which three launches in a row never reported v2.1.1 (the user only found it via the manual check, 58 s after the Release went live). Do not reintroduce a success-side cache.
- **Only failures are throttled**: every failure branch (HTTP error, 403/429 rate limit, timeout, `HttpRequestException`, `JsonException`, unparsable tag, unknown) funnels through `FailAsync`, which — for silent checks only — arms a **1 h backoff** via `Update.SilentFailureUtc`. A successful silent check clears the mark. This is the only thing standing between "check on every launch" and a request storm against the 60/hour anonymous API cap, so no failure branch may bypass it, and the window must not be widened back to 24 h.
- **`manual: true` writes nothing.** Manual checks always hit the network and neither read nor write any throttle key — otherwise a manual check would push the next silent check back by a full window (the bug the user hit).
- Silent checks must never surface errors — failures log at `Debug` and return a `Failed` result the caller ignores. Never log the raw response body. `Update available: …` (Information) is the single line log-based diagnosis relies on — keep it, and keep the backoff skip at `Debug`.
- Settings keys (all **strings**, because `ISettingsService` only has `int`/`string` overloads — do not extend that interface for this):
  - `Update.SilentFailureUtc` — ISO 8601 round-trip timestamp of the last **failed** silent check; deleted by the next successful silent check.
  - `Update.LastCheckUtc` — **removed in v2.1.2** (it held the last *successful* check time). Nothing may read it again; `UpdateService` deletes the stale key once per session, best-effort.
  - `Update.SkippedVersion` — the version the user chose to skip (silent checks stop nagging; manual checks still report it).
- Runtime cadence lives in `MainWindow`, not in the service: `StartRuntimeUpdateCheckTimer()` (first activation) arms a repeating `DispatcherQueueTimer` (`RuntimeUpdateCheckInterval`, 24 h) whose tick reuses `RunSilentUpdateCheckAsync(isStartup: false)`, and the window's `Closed` handler stops it. Keep the timer on the UI thread — a hit has to raise a `ContentDialog` and must share the single `_isUpdateDialogOpen` guard with the manual path. Keep the service ignorant of *when* it is called.
- The installer asset is picked from `assets[]` as the `.exe` whose name contains `Setup`. The portable ZIP is intentionally never used for auto-update.

### Update install (`UpdateInstallerService`)

- Downloads to `%TEMP%\SerialPortTool\Update` with `HttpCompletionOption.ResponseHeadersRead` (streaming, so memory use is independent of package size) and reports throttled progress through `IProgress<double>`.
- **Verification is mandatory before launching**: non-empty, length equal to the asset `size` (when known), and an `MZ` PE header. Without it a 403/HTML error page would be executed as an installer.
- The downloaded installer *is* the external updater process, launched with `/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL`. That is why no bespoke updater executable exists: a hand-rolled one would add antivirus false positives, elevation problems, and half-written app directories.
- **`/RESTARTAPPLICATIONS` is deliberately not passed (v2.0.2).** It maps to `[Setup] RestartApplications`, which wraps Restart Manager's `RmRestart` and only relaunches processes Restart Manager itself closed during that Setup run. In the auto-update path the app is *already gone* (`LaunchInstaller()` → `MainWindow.Close()` → `App.OnWindowClosed` → `Environment.Exit(0)`), so there is nothing to close and therefore nothing to relaunch — the install finished silently and the app never came back. The restart is owned by `installer/SerialPortTool.iss`: `RestartApplications=no` + an explicit `Exec` of `{app}\SerialPortTool.exe` from `[Code] CurStepChanged(ssPostInstall)` when `WizardSilent()`. The interactive path keeps using the `[Run]` entry (which carries `skipifsilent`, so it cannot double-launch in silent mode).
- Auto-replacement is gated on `InstalledBuildInfo.IsInstalled()`: the running exe must live under `%LOCALAPPDATA%\Programs\SerialPortTool` **and** the Inno Setup uninstall key must exist. A portable ZIP build therefore only gets a notification plus the download page — it must never silently overwrite a user-chosen folder.
- Exit reuses the existing shutdown path: flush settings (`ISettingsService.FlushAsync()`) → `LaunchInstaller()` → `MainWindow.Close()` → `App.OnWindowClosed` (5 s hard timeout, `Environment.Exit(0)`). Do not introduce a second exit mechanism.
- `IProgress<double>` callbacks are produced off the UI thread, so they are marshalled back with `DispatcherQueue.TryEnqueue` before touching the `ProgressBar`.

### Installer (`installer/SerialPortTool.iss` + `scripts/build-installer.ps1`)

- `PrivilegesRequired=lowest` → per-user install into `%LOCALAPPDATA%\Programs\SerialPortTool`, no administrator rights and no UAC prompt. That is what makes a fully silent update possible; moving to `Program Files` would trigger a UAC prompt on every update.
- The **`AppId` GUID is permanent**. It must stay identical to `InstalledBuildInfo.AppId` (`Services/UpdateInstallerService.cs`); changing either turns every upgrade into a side-by-side install and orphans the previous one in "Apps & features".
- **`AppDisplayName` is `SerialPortTool`** (v2.2.1) and may change freely — unlike `AppId`, it is not an identity. It feeds `AppName` / `AppVerName`, `DefaultGroupName` (the Start-menu folder), the `[Icons]` shortcut names, the uninstall entry and the Setup window caption. It used to be `串口工具 (SerialPortTool)`; `LegacyAppDisplayName` plus two `[InstallDelete]` entries exist **only** to remove that old Start-menu group and desktop shortcut during an upgrade, because a same-`AppId` install does not reclaim icons it no longer lists (the paths must be hard-coded: `{group}` / `{autodesktop}` have already resolved to the new name by then). If the display name changes again, move the previous value into `LegacyAppDisplayName` and extend those entries instead of leaving both names on disk.
- `AppVersion` is injected on the command line by `scripts/build-installer.ps1` (sourced from `version.json`) — never hard-code a version inside the `.iss`. There is **no fallback define**: `#ifndef AppVersion` is a `#error`, because compiling the `.iss` directly used to produce a `0.0.0` package, and `UpdateService` compares versions numerically — such a package permanently breaks the update path on every machine that installs it. `build-installer.ps1` also asserts the version is three numeric parts before invoking ISCC.
- `MinVersion` is `10.0.17763` (Windows 10 1809), matching `TargetPlatformMinVersion` in the csproj. `MinVersion=10.0` let the installer succeed on 1607/1709 and the app then crashed on first launch — and because the files were already on disk it read as an application bug rather than "your Windows is too old".
- The **framework language folders** are pruned from the publish directory by `scripts/prune-publish-output.ps1`, which `build-installer.ps1` runs before ISCC and `.github/workflows/release.yml` runs before zipping. That script is the single source for both the kept list (`zh-CN`, `en-us`) and the classification rule; the two call sites used to keep their own copies of the list and the pattern, which is exactly the kind of duplication that drifts. Because pruning happens on the *publish* tree, the installer payload and the portable ZIP are identical by construction. `[Files] Excludes` is now only the `*.pdb` guard.
- The classification is **name AND content**, not name alone: a top-level folder is a language folder only if its name looks like a BCP-47 tag (`^[a-z]{2,3}(?:-[A-Za-z0-9]+)*$`) **and** every file inside it is a `.mui` or `*.resources.dll`. A name-only rule is not safe to apply blindly to a build output — `de`, `lib`, `sdk`, `www` all match the name pattern, and deleting `lib` would break the app with no error anywhere. The content check is what makes it a deletion rule rather than a guess.
- Measured on a real Release publish (validated against `scripts/prune-publish-output.ps1`): 89 top-level folders / 456 files / 165.9 MB before, 5 folders / 287 files / 164.1 MB after. The 84 removed folders match the "84 empty `xx-YY`" figure below. Note the surviving names are `Assets`, `en-us`, `Microsoft.UI.Xaml`, `runtimes`, `zh-CN` — this project's publish output contains **no** bare two-letter language folders, so a rule that also tolerated them was never needed; the earlier belief that they leaked through was not reproducible.
- `[Files]` must **not** carry `createallsubdirs`. Inno Setup skips empty directories by default, but that flag also creates directories that became empty *because of* an exclusion, which leaves 84 empty `xx-YY` folders in the install directory and defeats the whole point of the exclusion. Measured on a real install: with the flag → 93 directories, 84 of them empty; without it → 9 directories, all populated (`Assets`, `Assets\Images`, `en-us`, `Microsoft.UI.Xaml`, `Microsoft.UI.Xaml\Assets`, `runtimes`, `runtimes\win-x64`, `runtimes\win-x64\native`, `zh-CN`). Verified install footprint: 289 files / 169.5 MB (287 payload + `unins000.exe` + `unins000.dat`).
- `[InstallDelete]` clears `{app}\*` before installing so assemblies and language resources dropped by a newer version do not linger. User settings (`%LOCALAPPDATA%\SerialPortTool`) and logs (`Documents\SerialPortTool`) live outside `{app}` and are preserved by design — uninstall must not delete them either.
- Compression is `lzma2/ultra64` + `SolidCompression`. The self-contained payload is several hundred MB, so the in-app download must keep its progress bar and cancel button.
- Inno Setup does not bundle a Simplified Chinese language file. `build-installer.ps1` detects `Languages\ChineseSimplified.isl` next to `ISCC.exe` and only then passes `/DIncludeChinese=1`, so the installer still compiles on a stock Inno Setup install (English UI).
- The installer does not change the publish settings. `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` remain required for WinUI 3 stability; the fix for "too many files in the install directory" is packaging, not trimming.
- `[Icons]` sets `IconFilename: "{app}\Assets\Images\logo.ico"` explicitly for both the Start-menu and the desktop shortcut. Without it the shortcut falls back to the exe's embedded icon, which couples "the shortcut looks right" to "the exe icon resource was embedded correctly" for no benefit — `logo.ico` already ships (it is under `Assets/`, which `[Files]` copies) and is the file the app itself loads at runtime.
- `[Code] CurStepChanged(ssPostInstall)` calls `SHChangeNotify(SHCNE_ASSOCCHANGED)` (declared as an `external` on `shell32.dll`) before the optional restart. See the "do not regress" entry for why; this must stay unconditional — the interactive path needs it just as much as the silent one.

---

## CI/CD and Release

**Workflow**: `.github/workflows/release.yml`, triggered by tags matching `v*`.

**Action runtimes** (v2.0.4): every official action must declare `runs.using: node24`, because a Node 20 action still *succeeds* — the runner force-migrates it and only prints a deprecation banner, so a stale version is easy to miss. The runtime does **not** advance together with the major number, which is the trap: `checkout` and `setup-dotnet` need `v5`, but the artifact pair needs one major more than the `v5` written next to them.

| Action | Node 24 floor | Runtime note |
| --- | --- | --- |
| `actions/checkout` | `@v5` | `v4` = node20 |
| `actions/setup-dotnet` | `@v5` | `v4` = node20; `v5` also dropped support for very old .NET versions (`9.0.x` is unaffected) |
| `actions/upload-artifact` | `@v6` | **`v5` = node20**; `v7` only adds the opt-in `archive: false` direct-upload mode (default is still zipped) |
| `actions/download-artifact` | `@v7` | **`v5` and `v6` = node20**; `v8` additionally makes a digest mismatch fail the job and stops decompressing by content type |

Node 24 actions require Actions Runner **v2.327.1+** (GitHub-hosted `windows-latest` / `ubuntu-latest` already qualify; a self-hosted runner must be upgraded first). `download-artifact@v5` also changed the output path of a **single artifact downloaded by ID** — this workflow downloads every artifact by path (`path: artifacts`), so it is not affected. Check a candidate version before trusting a release note: `https://raw.githubusercontent.com/actions/<name>/<tag>/action.yml`, field `runs.using`.

Build job: validate `version.json` against the git tag → generate `BuildInfo.g.cs` → restore/build for x64 Release → self-contained publish (no R2R / single-file / trimming) → **assert that `Package.appxmanifest` and the published `SerialPortTool.dll` carry the tag version** → **install Inno Setup (chocolatey) and run `scripts/build-installer.ps1 -SkipPublish`** (which prunes the publish tree first) → package a portable ZIP using the same `scripts/prune-publish-output.ps1` → assert the ZIP is not empty → upload the ZIP **and the Setup.exe** as artifacts.
Release job: generate release notes from the `version.json` changelog → copy the artifacts into `release-assets/`, **asserting that both a `.zip` and a `.exe` are present** → create or update the GitHub Release with both.

The three assertion groups exist because each failure mode used to publish a Release anyway:

- **manifest / assembly version** — the manifest sync script used to `exit 0` when it could not find the `Identity` element, and the csproj had a hard-coded version fallback. Either one produces a build whose About dialog, file properties and `Package.appxmanifest` disagree with the tag, with nothing red in the log.
- **ZIP not empty** — `Compress-Archive` writes a perfectly valid archive from an empty directory; the only symptom would be a portable download that unpacks to nothing.
- **a `.exe` in `release-assets/`** — the old check rejected only a completely empty set, so a run where the installer step produced nothing still published a Release with a ZIP and no `Setup.exe`, leaving the in-app auto-update (which looks for the `Setup*` asset) with nothing to download.

> `scripts/build-installer.ps1` publishes to `publish/x64` itself when run without `-SkipPublish`, so the workflow reuses the already-published output and never produces a second, differently-configured build.

**To release**:
1. Update `version.json` (version + changelog) — or run `.\scripts\bump-version.ps1 -BumpType patch`, which also commits and tags.
2. `git push` the commit, then `git push origin v<version>`.
3. GitHub Actions builds and publishes the release.

---

## Important Constraints

- **Platform**: x64 Windows only (ARM64 support removed in v1.4.0).
- **Minimum Windows version**: 10.0.17763 (Windows 10 1809). Enforced in two places that must stay in sync: `TargetPlatformMinVersion` (csproj) and `[Setup] MinVersion` (installer).
- **Single instance**: exactly one process per user session. The guard is a named mutex in `App`'s constructor, taken before `App.xaml` is loaded; a second launch logs a warning, shows a message box and exits. Activation-argument forwarding (`AppInstance.RedirectActivationToAsync`) is deliberately **not** implemented — it would add a dependency on `Microsoft.Windows.AppLifecycle`'s unpackaged behaviour for no functional gain.
- **Publish settings**: `PublishTrimmed=false`, `PublishReadyToRun=false`, `PublishSingleFile=false` — required for WinUI 3 stability.
- **Language**: C# 13 (the .NET 9 SDK default; `LangVersion` is not pinned). `[ObservableProperty]` is applied to backing fields, not partial properties.
- **Packaging**: unpackaged (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`), distributed either as a per-user Inno Setup installer or as a portable ZIP.
- **Auto-update scope**: silent file replacement runs **only** for the installed build (`InstalledBuildInfo.IsInstalled()`). The portable ZIP must never be replaced in place — it only shows a notification and opens the download page.
- **No update framework**: the update feature uses only `System.Net.Http` + `System.Text.Json` from the BCL. `Newtonsoft.Json` stays removed (v1.8.5); do not reintroduce it for this path.
- **Installer identity**: the `AppId` GUID is shared between `installer/SerialPortTool.iss` and `InstalledBuildInfo.AppId`. They must stay in sync, and the GUID must never change after a release.

---

## Debugging

- **Application logs**: `%USERPROFILE%\Documents\SerialPortTool\DebugLogs\app-<date>.log` (daily rolling, 7-day retention, 50 MB cap per file — configured in `App.xaml.cs`). "工具 → 打开日志文件夹" opens the folder.
- **The File sink is `shared: true`.** Without it only the first process can open the daily file and Serilog's File sink swallows its own failures, so a second instance logged *nothing at all* — the reason a duplicate launch used to look like "the app started and the log is empty".
- **Log level**: `Information` (`App.xaml.cs`).
- **A rejected duplicate launch is in the log**: `TryAcquireSingleInstanceMutex` runs before `InitializeComponent()`, so the second process writes a `Warning` before exiting. If a launch produces no window, that is the first thing to look for.
- **Settings are read-only after a parse failure**: `%LOCALAPPDATA%\SerialPortTool\settings.json` is left exactly as-is and the status bar reports it. A user who wants a clean slate must fix or delete the file (or use `ISettingsService.ClearAsync()`).
- **Global exception handlers** (`App.xaml.cs:81-98`) — three channels, all three are needed:
  - `Application.UnhandledException` → logged at `Fatal`. This is the only channel that sees an exception the XAML framework raises on the UI thread (binding evaluation, template instantiation, window construction). It was added in v2.1.0 after a startup failure produced a completely empty log.
  - `AppDomain.UnhandledException` → background-thread exceptions. Do not remove it; the app used to terminate silently on those.
  - `TaskScheduler.UnobservedTaskException` → logged and marked observed.
- **Serilog is configured, and the handlers are registered, *before* `InitializeComponent()`.** Two reasons, both load-bearing:
  1. `Application.LoadComponent` is where `App.xaml` and its merged theme dictionaries are realised, so a broken dictionary would otherwise die with an empty log.
  2. The XAML compiler generates its own `UnhandledException` subscriber inside `App.InitializeComponent()` (`App.g.i.cs`, guarded by `DEBUG && !DISABLE_XAML_GENERATED_BREAK_ON_UNHANDLED_EXCEPTION`) whose body is `if (Debugger.IsAttached) Debugger.Break();`. Event handlers run in registration order, so registering ours **after** it means the debugger stops the process before a single line reaches the log. If you ever see a stack whose only managed frame is `App.InitializeComponent.AnonymousMethod__…` at `App.g.i.cs:69`, that is this generated hook — it tells you an exception went unhandled but not what it was; check the log for the `Fatal` entry.
- **Failed shell setup is not a crash.** `MainWindow.GuardShellStep` (and the try/catch around `ExtendsContentIntoTitleBar`/`SetTitleBar`, which rolls the extension back on failure) logs a `Warning` and keeps going. `AppWindow.TitleBar`, `SystemBackdrop` and `ExtendsContentIntoTitleBar` are the most capability-sensitive APIs in the app — unavailable on Windows 10, disableable by policy or by the transparency-effects setting, and known to throw on some virtualised GPUs — and none of them is load-bearing. A window with plain chrome is always preferable to a window that never appears.

---

## Reliability Mechanisms (do not regress)

Each of these exists because a specific bug caused a crash or an error storm; removing them looks like simplification right up to the next incident.

- **Settings file lock** (`SettingsService`, v1.8.10) — serializes writes to `settings.json` so the tuning watcher and a port-open path cannot corrupt it.
- **Settings read-only protection** (`SettingsService`, v2.1.1) — a `settings.json` that exists but cannot be read or parsed sets `_loadFailed`, and every write path (cache-fill, delete, flush) refuses to persist from then on, reporting once through `SettingsLoadFailed`. The old behaviour assigned an empty dictionary to the cache and flushed it on the next 500 ms tick, replacing the user's port configs, appearance, colours and search history with `{}`. A zero-byte file counts as a failure too — that is the classic half-written result of the non-atomic writer this pairs with.
- **Atomic settings write** (`SettingsService`, v2.1.1) — serialize to `settings.json.tmp` in the same directory, then `File.Move(tmp, dest, overwrite: true)`. Same-volume rename is atomic, so a crash, power loss or full disk leaves either the complete old file or the complete new one, never a truncated one. `UnauthorizedAccessException`/`IOException` are handled explicitly and leave the original untouched. Do not go back to `File.WriteAllText` on the real path — that is what *created* the truncated file the read-only protection above has to cope with.
- **Reconnect cooldown** (`SerialPortService`, v1.8.10) — a failed reconnect enforces backoff; without it a yanked port produces thousands of `UnauthorizedAccessException`.
- **Port reopen retry** (`SerialPortService` / `PortInstance.Dispose`, v1.8.11) — on close, `_isClosing` is reset, availability is re-checked while the OS releases the COM handle, and the cleanup delay lives in `Dispose`'s `finally` so it also runs on exception paths.
- **No sync-over-async teardown** (`SerialPortService`, v2.1.1) — `IAsyncDisposable.DisposeAsync` is the real path; the synchronous `Dispose()` closes each port directly. Do **not** reintroduce `Task.Run(Close).Wait(timeout)`: the timeout bounds nothing (the task keeps running and the port gets disposed underneath it) and the abandoned work is what leaked COM handles. `_ports` is never cleared without disposing the instances it holds.
- **`_writeLock` / `_writeCts` are intentionally not disposed** (`PortInstance.Dispose`, v2.1.1) — disposing the write semaphore raced in-flight sends, which then threw `ObjectDisposedException` from their own `finally { _writeLock.Release(); }`. That single line was the shutdown crash. Neither object holds an unmanaged resource in this usage (`AvailableWaitHandle` is never touched, no `CancelAfter`), so letting the GC collect them with the `PortInstance` is correct — and cannot throw.
- **Validation queue lock** (`DataValidationService`, v1.8.10) — the per-port `PortValidationState` queue stays locked. Validation now runs inline on the read thread, but `ResetValidationState` can still be called from the UI thread.
- **Tuning send pre-check** (`MainViewModel.SendTuningToPortWorkerAsync`, v1.8.10) — `IsPortOpen` is checked before every tuning send, required because auto-send can fire mid-reconnect and caused `CancellationTokenSource` disposal crashes. It lives in the ViewModel, not in `TuningProtocolService` (which only builds frames).
- **Single-instance mutex** (`App`, v2.1.1) — one process per user session, taken before `App.xaml` loads. Two instances fight for the same COM handles and, because `SettingsService`'s lock is process-local, overwrite each other's `settings.json`. The mutex is deliberately never released: the OS closes the handle when the process dies, including on a crash, whereas releasing it on window close would let a second instance start during teardown.
- **Idempotent logger disposal** (`FileLoggerService.LoggerInstance`, v2.1.1) — `DisposeAsync` runs once; a second caller awaits the first instead of re-disposing the writer and timer. Combined with `_lifecycleLock` around Start/Stop, this closes the check-then-act window that used to leak a `LoggerInstance` (a 64 KB `StreamWriter` file handle plus a 100 ms timer) whenever two starts raced for the same port.
- **Port registration is atomic** (`SerialPortService.OpenPortAsync`, v2.1.1) — `_ports.TryAdd`; the loser of a concurrent open disposes its own instance. The `ContainsKey` check at the top is separated from the registration by the availability probe and up to three open attempts, so the indexer assignment silently dropped the loser's open `SerialPort`.
- **One open request per port** (`MainViewModel._openingPorts`, v2.1.1) — `OpenPortCommand.ExecuteAsync` is also called directly from the port list's `SelectionChanged`, which bypasses `CanExecute` entirely; the atomic claim in `finally` is what prevents a double-open.
- **Clipboard failures are caught** (`Controls/LogListView.xaml.cs`, v2.1.1) — `Clipboard.SetContent` throws `COMException` whenever another process holds the clipboard open, which is routine. Unhandled it reached the XAML unhandled-exception handler and terminated the process, reachable by accident through a toolbar button and Ctrl+C. The failure now raises `CopyFailed` and becomes a status message.
- **`LogListView` re-subscribes on `Loaded`** (`Controls/LogListView.xaml.cs`, v2.1.1) — the `ItemsSource` dependency property is not re-assigned across an unload/load cycle, so the property-changed callback does not run again and the `CollectionChanged` subscription (auto-scroll) stayed gone for the rest of the session.
- **Wheel scrolling is owned by `LogListView`** (`Controls/LogListView.xaml.cs`, v2.1.5) — the log rows are ~20 px tall, so the framework's per-notch step moved the view by barely a row and read as "the wheel is dead"; it reproduced at a few dozen lines/s and identically while paused, i.e. it was never about throughput. The primary `PointerWheelChanged` registration must stay on the **ScrollViewer's content** (the items presenter) with `handledEventsToo: false`: it has to run before the ScrollViewer's own class handler for `e.Handled = true` to suppress that handler's step. The `ListView`-level registration with `handledEventsToo: true` is only the fallback for gestures that skip the items presenter (they hit-test the ScrollViewer directly) and must keep its `if (e.Handled) return;` guard. Moving the primary registration up to the `ListView` stacks the framework step on top of ours; dropping the `Unloaded` detach doubles our step on every unload/load cycle (the template hands out a new ScrollViewer, the container is reused); and the step constants are derived from the row height, so a taller row means re-checking `MinWheelStepPixels`.
- **Filter re-entry has one entry point** (`MainViewModel.RequestFilterLogs`, v2.1.1) — the search dropdown selection, the dropdown closing and the Enter key each called the synchronous O(n) rebuild *on top of* the debounce their own `SearchText` assignment had already armed. `FilterLogs()` is private now; call `RequestFilterLogs()`.
- **Search filters on commit only** (`MainViewModel.SearchDraft` / `SearchText`, v2.1.4) — the box writes `SearchDraft`; only `ExecuteSearchCommand` assigns `SearchText`. That single assignment is what re-filters, what the flush loop snapshots for newly arriving lines, and what records history. Merging the two back together restores "every keystroke rebuilds ~2000 rows" *and* lets live traffic be filtered by a pattern the user has not finished typing.
- **`WaitForStableTuningFileAsync` reports instability** (`MainViewModel`, v2.1.1) — it returns `false` instead of falling through silently after the retry budget. Hashing a `.bin` that is still being written stored a baseline for content that no longer existed, which suppressed every later auto-send as "content unchanged".
- **Hex input is parsed per group** (`MainViewModel.TryParseHexInput`, v2.1.1) — a global `Replace("0x", "")` corrupted any payload containing those characters (`A0x0B` → `A0B`). The prefix is per-group notation and is only stripped at the start of a group.
- **`checked` arithmetic in the tuning builder** (`TuningProtocolService`, v2.1.1) — expression evaluation and checksum accumulation are `checked` and translate overflow into `TuningProtocolException`. An unchecked wrap in a length expression produces a header whose declared total does not match the bytes sent, which fails on the device rather than at build time.
- **No hard-coded version fallback** (`SerialPortTool.csproj`, v2.1.1) — the `ValidateVersion` target fails the build when `version.json` cannot be parsed. The fallback was `1.7.0` while the project was on 2.1.0, i.e. a silently mis-versioned build, and the version regex is anchored to the opening brace so a reordered top-level key cannot make it pick up a changelog entry.
- **Installer refuses a version-less build** (`installer/SerialPortTool.iss`, v2.1.1) — `#ifndef AppVersion` is a `#error`. The old `0.0.0` fallback produced an install package that permanently breaks auto-update on every machine that installs it, because versions are compared numerically.
- **Single-threaded per-port decode/validation** (`SerialPortService`, v1.8.13) — `SerialPort_DataReceived` awaits validation before reading the next chunk, and a garbage verdict arms a ~1 s drop cooldown, so the per-port `Decoder`/`StringBuilder` is only ever touched by one thread. Do not make validation fire-and-forget again.
- **Shutdown timeout** (`App.xaml.cs:196-242`) — window-close cleanup runs on a thread-pool task with a hard 5-second wall clock; on timeout the app force-exits instead of hanging on a stuck COM handle.
- **Installer verification before launch** (`UpdateInstallerService`, v2.0.0) — the downloaded `Setup*.exe` is rejected unless it is non-empty, matches the Release asset `size`, and starts with `MZ`. Launching an unverified download would execute a 403/HTML error page as an installer on a bad network.
- **Installed-build gate** (`UpdateInstallerService.InstalledBuildInfo`, v2.0.0) — auto-replacement requires both the `%LOCALAPPDATA%\Programs\SerialPortTool` location **and** the Inno Setup uninstall key. Removing the gate would let the app silently overwrite a user's portable folder.
- **Silent-check log level** (`UpdateService`, v2.0.0; backoff reworked in v2.1.2) — failures of the automatic check log at `Debug` only, and only a *failure* throttles the next attempt (1 h, `Update.SilentFailureUtc`). Raising the log level produces an error storm whenever the machine is offline; removing the failure backoff burns through GitHub's 60/hour anonymous limit as soon as the machine is offline or rate-limited. Note the two mechanisms are deliberately asymmetric: a *success* must never suppress the next check (v2.1.2 — a 24 h success-side cache hid v2.1.1 from three consecutive launches of v2.0.4), so do not "restore" symmetry by also throttling on success.
- **Explicit silent restart** (`installer/SerialPortTool.iss`, v2.0.2) — `RestartApplications=no` plus `[Code] CurStepChanged(ssPostInstall)` → `Exec` of the app under `WizardSilent()`. `RestartApplications=yes` reads like the obvious "restart after update" switch, but it only restarts what Restart Manager closed in the same session, and auto-update exits the app first — so the update installed cleanly and nothing was relaunched. The `[Run]` entry cannot cover silent mode either (`skipifsilent`). Do not re-add `/RESTARTAPPLICATIONS` to `UpdateInstallerService.SilentInstallArguments`: the command-line flag overrides the directive.
- **Shell icon refresh after install** (`installer/SerialPortTool.iss`, v2.0.3) — `[Code] CurStepChanged(ssPostInstall)` calls `SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, 0, 0)` before the restart. Windows caches shortcut/file icons in `iconcache_*.db` *and* in Explorer's in-memory system image list; a silent update replaces files at the same paths while the app exits immediately, so Explorer never re-reads them and the desktop/Start-menu shortcut keeps rendering the previous release's icon (reproduced v2.0.1 → v2.0.2, with the new multi-resolution ico already correctly embedded in the exe). Inno's own per-shortcut notification only tells Explorer that *the .lnk* changed, which is not enough to force a re-extract. Note the fix is a notification, not a differently-named icon file: the cache is keyed by source path, so renaming the ico does nothing except make the shortcut point at a file nothing else uses.
- **Flush-before-handoff** (`MainWindow.DownloadAndInstallAsync`, v2.0.0) — `ISettingsService.FlushAsync()` completes before the installer is launched. Skipping it loses the last ~500 ms of debounced settings (including `Update.SkippedVersion`) on every auto-update.
- **Installer / ZIP language-list parity** (`scripts/build-installer.ps1` + `.github/workflows/release.yml`, v2.0.0) — both artifacts keep only the `zh-CN` and `en-us` framework language folders, and the `[Files]` entry must stay free of `createallsubdirs` so the excluded folders do not reappear as empty directories. This degrades silently rather than crashing: drop either half and ISCC still compiles without a warning — the installer just packs 168 extra `.mui` files and/or recreates 84 empty `xx-YY` folders. The kept-language list is duplicated (Inno Setup cannot read the workflow file), so change both sides together. A `Compressing:` line count from the ISCC log catches the first half; only a real install catches the second.
- **Appearance applied before the first frame** (`App.OnLaunched` → `App.InitialThemePreference` → `MainWindow` constructor, v2.1.0) — reading the `AppTheme` setting inside `MainWindow`, or applying it after `Activate()`, repaints the window light for one frame on every launch of a dark-theme install. The read must stay ahead of window resolution, and the *resolved* darkness must be sampled from `RootLayout.ActualTheme` rather than from the preference, otherwise 跟随系统 never resolves to dark.
- **Opaque background under the backdrop** (`MainWindow.SetupBackdrop`, v2.1.0) — `RootLayout.Background` is cleared **only** when `MicaController.IsSupported()`; the XAML default is the opaque token brush. Clearing it unconditionally renders uninitialised memory on Windows 10, when transparency effects are switched off, and on some virtualised GPUs.
- **Log rows are re-coloured in place** (`MainViewModel.ApplyEffectiveTheme`, v2.1.0) — the appearance sweep mutates `LogEntry.ColorHex` across `AllLogs`; it must never replace the `DisplayLogs` instance, the same rule the filtering path follows.
- **`ColorHex` is `OneWay`, `FormattedText` stays `OneTime`** (`Controls/LogListView.xaml`, v2.1.0) — collapsing `ColorHex` back to `OneTime` strands already-rendered rows on the previous appearance's brush, and it fails *unevenly*: containers recycled during scrolling pick up the new colour while the rest keep the old one, so the log ends up mixing two palettes. The opposite mistake — promoting `FormattedText` to `OneWay` — pays `PropertyChanged` wiring for a string that never changes.
- **A close wins over an in-flight reconnect** (`PortInstance.RequestTeardown` / `OpenAsync`, v2.1.3) — `ReconnectAsync` is `CloseAsync` → 500 ms delay → `OpenAsync` on the same instance, and `SerialPortService.ClosePortAsync` removes the instance from `_ports` at the *start*, so the reconnect was the one who decided whether the port came back. If the user closed the port inside that window (routine with the default `AutoReconnect = true` and a wrong baud rate, whose Frame-error storm reconnects every few seconds) the port was reopened, but by then it was in neither `_ports` nor `OpenPorts`: an open COM handle that nothing in the app could close, so the user had to exit the process to free the port. `RequestTeardown()` (called by `ClosePortAsync`, `DisposePortInstance` and the duplicate-instance discard) sets a flag and takes the handle, `OpenAsync` publishes `_serialPort` under `_lifecycleLock` only when the flag is clear — and closes what it just opened when it is not — and `ReconnectAsync` returns `bool` instead of reopening blindly. Do not "simplify" this back to a plain `_serialPort = port` assignment, and do not drop the lock: the flag and the handle have to be decided together, or a teardown that looks at `_serialPort` between the check and the assignment sees `null` and the handle leaks again.
- **Baud-rate scans are cancelled by a close** (`MainViewModel._baudRateDetections`, v2.1.3) — the scan closes the port for up to ~40 s and reopens it at the end, all outside `SerialPortService`. A close that raced it therefore either reported success while the detector still held the handle, or was undone by the reopen — both ending the same way as above, with a port only the process exit could free. Close paths now cancel the scan (which closes the detector's own handle on the way out), `ReopenPortWithBaudRateAsync` skips a port that is no longer in `_portsByName`, and one scan per port is enforced (`TryAdd`) so the quality watchdog cannot stack close/reopen cycles. The entry is removed by the scan in its own `finally`, not by the canceller — removing it early would let a second scan start while the cancelled one was still releasing its handle.
- **Port maps and port-name comparisons are case-insensitive** (`SerialPortService`, v2.1.3) — `_ports`, `_lastReconnectAttempt` and `IsListed` use `OrdinalIgnoreCase`. COM names are case-insensitive at the OS level, so two spellings of the same port are the same port everywhere except in a default-comparer dictionary: `_ports.TryRemove("COM3")` against a key of `"com3"` missed, and the close *reported nothing at all* — no exception, no error, just a port that stayed open after its row was gone. `OpenPortAsync`'s availability probe had the mirror-image defect (`List<string>.Contains` is ordinal too): a persisted lowercase name was refused as "not available in the system" after the 1.5 s wait although the port was right there.
- **A close-all undoes opens that were already in flight** (`MainViewModel._closeAllEpoch`, v2.1.3) — an open can spend seconds inside `SerialPortService` (availability probe + up to three attempts with 500 ms backoff). 全部关闭 pressed inside that window had nothing to close — the port is in neither the service map nor `OpenPorts` yet — so it completed "successfully" and the port appeared a moment later, open. Both open paths sample the epoch before and compare it after, and close what they brought up instead of adding a row. Note the counter covers *per-port* closes too only because the UI cannot issue one: the close button lives on the port's row, which does not exist until the open returns.
- **An open never lands on a torn-down service** (`SerialPortService.OpenPortAsync`, v2.1.3) — an open already in flight when the container is disposed (app shutdown) used to `TryAdd` into `_ports` *after* `DisposeAsync` had cleared it, registering a handle no remaining code path would close. Both the early `_disposed` check and a second one immediately before the `TryAdd` are needed: the first is about not starting new work, the second about not publishing finished work.
- **Close the port before stopping its file logger** (`MainViewModel.ClosePortAsync` / `CloseAllPortsAsync`, v2.1.3) — `IFileLoggerService.StopLoggingAsync` awaits the port's writer, and it used to run *first*, so a slow (redirected / OneDrive-synced / busy) log volume delayed or — with a wedged write — permanently prevented the close while the UI claimed to be closing the port. The port is what the user asked to close; the log teardown is bookkeeping and goes after it. This also captures the tail of the log instead of truncating it.

---

## Notable User-Facing Features (architectural)

Only the ones that change how you should reason about the code — `version.json` has the full changelog.

- **Multi-port management** — open/close individual ports, "open all"/"close all", "scan ports".
- **Per-port colours** (v1.7.0, extended v2.1.0 / v2.1.1) — each opened port gets a unique colour from a 10-colour slot palette, plus a configurable TX colour; both are persisted as slots and resolved per appearance. `LogEntry.ColorHex` holds the colour **resolved for the active appearance**, and the rendering path is always `HexColorToBrushConverter` (log rows in `LogListView.xaml`'s `DataTemplate`, the sidebar swatch, the channel legend). New `LogEntry` fields needing colour treatment must go through the same converter. Changing a port's colour re-colours the rows already on screen (`ApplyPortColorChange`), the same way an appearance switch does — only `IsReceived` rows are touched, because sent/tuning rows carry the TX colour.
- **Single instance** (v2.1.1) — a second launch shows a message box and exits. See Important Constraints and the "do not regress" entry.
- **Send box Enter** (v2.1.1) — Enter in the send box sends to every open port, which is what the placeholder text always claimed. The text box is single-line, so Enter has no other meaning.
- **Appearance switch** (v2.1.0) — 跟随系统 / 浅色 / 深色 from the 外观 menu, persisted as `AppTheme` and applied as `ElementTheme` on the window root. Required reading before touching anything visual: see "UI and Appearance".
- **Channel trace + channel legend** (v2.1.0) — every log row carries a 3 px bar in its port's colour, and a legend strip above the log states the colour → port → byte-count mapping, so interleaved multi-port traffic stays attributable at a glance.
- **Log pause toggle** — pauses UI appending without stopping reception; buffering continues while paused and batched updates resume on unpause. Anything touching the data-flow pipeline must respect this.
- **Search** (v1.5.0 / v1.6.2, reworked v2.1.4) — 文本 / 正则 and 区分大小写 switches (both persisted as `SearchUseRegex` / `SearchCaseSensitive`), commit-based filtering (Enter / 搜索) rather than as-you-type, and history in a flyout behind an explicit 历史 button with per-item delete and a confirmed clear-all. Text mode is the default, so regex metacharacters are literal there and an invalid pattern can only come from regex mode.
- **Baud-rate mismatch banner** — surfaced when detection confidence is high, with one-click correction.
- **Tuning broadcast** — see the Tuning/TOTA section.
- **Check for updates / auto-update** (v2.0.0) — "Help → Check for updates" for a manual check, plus a silent check a few seconds after the window is first activated. Finding a newer version opens a `ContentDialog` with the release notes and the release page. `ContentDialog` only has Primary / Secondary / Close slots, so the buttons are apportioned per scenario: installed + silent → "Download and install / Skip this version / Later"; installed + manual → "Download and install / Open download page / Close"; portable (cannot self-install) → "Open download page / Skip this version (silent only) / Later". Dialogs are built in code-behind following the `About_Click` pattern, with `XamlRoot = Content.XamlRoot` and a re-entrancy guard because WinUI 3 cannot show two `ContentDialog`s at once.

---

## Data Flow: Receiving Serial Data

```
Hardware serial port (the driver's DataReceived worker thread — one per port)
  ↓
PortInstance.SerialPort_DataReceived
  · reads into a reused scratch buffer, copies out an exact-sized chunk
  · awaits DataValidationService.ValidateDataAsync INLINE (no Task.Run) so the per-port
    state and the decoder stay single-threaded; a garbage verdict arms a ~1 s drop cooldown
  ↓
SerialPortService.DataReceived event (still the read thread)
  ↓
MainViewModel.OnDataReceived
  · persistent per-port UTF-8 Decoder + PortLineAssembler → complete lines only
  · garbage-line filter, 1000-char truncation, 500-line cap per chunk
  · FileLoggerService.WriteLogs(portName, batch)   →  disk, batched, background
  ↓
_pendingLogBatches queue + SchedulePendingLogFlush (one Low-priority dispatcher item)
  ↓
FlushPendingLogBatches (UI thread, ≤ 100 entries per 50 ms tick)
  · AllLogs.AddRange / DisplayLogs.AddRange (RangeObservableCollection, one notification)
  · TrimDisplayLogs / TrimLogCollection
  · throttled per-port statistics + traffic totals (~4 Hz)
  ↓
Controls/LogListView.xaml (virtualized ListView, compiled x:Bind)
```

Locally generated entries (TX, tuning summaries) enter the same queue through `AddSentLog`, so they are subject to the same filter, trim and batching.

---

## Known Issues and Limitations

- **Port close reliability** — Windows can hold a COM handle after close. Handled by retry + cleanup-delay in `finally`; read "Reliability Mechanisms" before touching `SerialPortService.PortInstance.Dispose`.
- **Very high baud rates** (>921600) — some data loss is possible; consider larger buffers in `SerialPortService`.
- **Complex regex** — heavy backtracking can hit the 100 ms timeout. Keep patterns simple for real-time filtering.
- **A wedged `SerialPort.Close()` cannot be recovered in-process** (v2.1.3) — `SerialPort.Close()`/`Dispose()` waits for the driver's internal event loop to exit, and a device that vanished mid-session (USB-serial adapter yanked, Bluetooth/virtual COM port removed, a driver stuck in `WaitCommEvent`) can leave that wait pending forever. Nothing in the app can then release the handle: `ClosePortAsync` never returns, `_serialPort` is already null so a retry has nothing to act on, and `Dispose()` on the same `SerialPort` waits on the same condition. The only recovery is ending the process — which is why `App.OnWindowClosed` bounds the whole teardown to 5 seconds and force-exits (`App.xaml.cs`), and why that bound must stay regardless of how clean the shutdown paths get. Distinguishing fingerprint in the app log: `Starting close sequence for port COMx` **without** a following `Port COMx fully closed and resources released`. This is not the same thing as a close that is silently ignored or undone — those were bugs and are covered by the "do not regress" entries above.
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
| `scripts/prune-publish-output.ps1` | Build / release tooling | The shipped language folders, `*.pdb`, or the publish-tree pruning step |
| Tuning / TOTA: the section above + `ViewModels/MainViewModel.cs` (`IsTuningEnabled`) + `MainWindow.xaml` (menu item, panel `Visibility`) | Users writing a tuning descriptor / maintainers | The descriptor format, the hidden-feature switch, or the send/watch flow |
