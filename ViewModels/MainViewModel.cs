using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.ViewModels;

/// <summary>
/// ObservableCollection that supports incremental batch operations without per-item notifications.
/// </summary>
/// <remarks>
/// AddRange / RemoveFromStart batch the underlying mutations and publish ONE notification at the
/// end. We previously tried firing a multi-item <c>Add</c> notification for smoother scrolling, but
/// WinUI 3's ListView throws "This collection cannot work with indices larger than
/// Int32.MaxValue - 1" when its internal vector-view tries to consume a multi-item Add — so
/// <c>Reset</c> used to be our only batch notification.
///
/// That reasoning was incomplete. Reset is cheap with respect to *item count*, but not with
/// respect to *frequency*: it tells ItemsStackPanel "contents unknown", so every realized
/// container is discarded and the visible window is rebuilt from scratch. At a 50ms flush cadence
/// that rebuild runs ~20x/sec even when a single line arrived — and the low-rate case is the one
/// this app normally runs in. The user-visible result is a log view that never settles under the
/// cursor while wheel-scrolling, despite data flowing at only a few dozen lines per second.
///
/// So the notification strategy is now adaptive (see <c>AddRange</c>): small increments go out as
/// single-item Add notifications, which the ListView consumes incrementally and which preserve the
/// scroll anchor; only large increments collapse into a Reset. Both shapes are accepted by the
/// auto-scroll path in <c>LogListView</c>.
///
/// The same frequency argument applies to trimming, and it used to be missed there: once DisplayLogs
/// reached its cap, <c>RemoveFromStart</c> published a Reset on *every* 50 ms flush — i.e. 20 full
/// list rebuilds per second, forever, which is its own "the view never settles" bug. Trimming now
/// overshoots on purpose (<see cref="MainViewModel.TrimDisplayLogs"/> trims well below the cap and
/// only fires once the list is well above it), which drops the rebuild rate to a fraction of a hertz.
/// A multi-item Remove notification would be the better fix because it keeps the scroll anchor, but
/// WinUI 3's vector view is known to mishandle multi-item collection notifications (that is why
/// multi-item Add was abandoned in the first place) and the failure mode is an unhandled exception
/// inside the ListView's own handler, which cannot be caught defensively. Until a spike proves the
/// multi-item Remove is safe on this framework version, the reset-plus-overshoot shape stays.
/// </remarks>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountPropertyChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerPropertyChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetEventArgs =
        new(NotifyCollectionChangedAction.Reset);

    /// <summary>
    /// Increments at or below this size are published as individual single-item Add
    /// notifications; anything larger collapses into one Reset.
    /// </summary>
    /// <remarks>
    /// 32 covers the normal low-rate case (a 50ms flush typically carries 1-5 lines) so the
    /// incremental path is taken almost always, while keeping the pathological worst case bounded
    /// at ~32 notifications per flush if a burst arrives while the user is mid-scroll.
    /// </remarks>
    private const int IncrementalAddThreshold = 32;

    private bool _suppressNotification = false;

    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) return;

        var itemsList = items as IReadOnlyCollection<T> ?? items.ToList();
        if (itemsList.Count == 0) return;

        CheckReentrancy();

        if (itemsList.Count > IncrementalAddThreshold)
        {
            // Bulk path: mutate silently, then publish ONE Reset.
            _suppressNotification = true;
            try
            {
                foreach (var item in itemsList)
                {
                    Items.Add(item);
                }
            }
            finally
            {
                _suppressNotification = false;
            }

            OnPropertyChanged(CountPropertyChanged);
            OnPropertyChanged(IndexerPropertyChanged);
            base.OnCollectionChanged(ResetEventArgs);
            return;
        }

        // Incremental path: one single-item Add per entry, so the ListView realizes only the new
        // rows and keeps its existing containers and scroll anchor intact.
        //
        // Items.Add() writes straight to the backing List<T> and bypasses InsertItem(), so no
        // notification is raised implicitly — we raise it explicitly. Single-item Add only:
        // WinUI 3's vector view mishandles multi-item Add payloads.
        _suppressNotification = true;
        try
        {
            foreach (var item in itemsList)
            {
                var index = Items.Count;
                Items.Add(item);
                base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add, item, index));
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(CountPropertyChanged);
        OnPropertyChanged(IndexerPropertyChanged);
    }

    /// <summary>
    /// Drops <paramref name="count"/> items from the head of the collection and publishes one Reset.
    /// </summary>
    /// <remarks>
    /// The Reset is not free — it discards every realized container and rebuilds the visible window —
    /// so callers must not run this at flush cadence. <see cref="MainViewModel.TrimDisplayLogs"/> is
    /// the only caller and deliberately overshoots so this runs a few times per minute rather than
    /// 20 times per second (see the type-level remarks).
    /// </remarks>
    public void RemoveFromStart(int count)
    {
        if (count <= 0 || Items.Count == 0) return;
        count = Math.Min(count, Items.Count);

        CheckReentrancy();

        _suppressNotification = true;
        try
        {
            // One O(n) shift instead of `count` separate O(n) shifts. The old loop moved the whole
            // tail of the list once per removed index — trimming 200 entries out of a 2200-item
            // ring meant ~440k element moves, on the UI thread, every trim.
            // ObservableCollection<T> always backs Items with a List<T>, so RemoveRange is
            // available; the fallback keeps correctness if that detail ever changes.
            if (Items is List<T> backing)
            {
                backing.RemoveRange(0, count);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    Items.RemoveAt(0);
                }
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(CountPropertyChanged);
        OnPropertyChanged(IndexerPropertyChanged);
        base.OnCollectionChanged(ResetEventArgs);
    }

    /// <summary>
    /// Removes a contiguous run in one silent mutation plus a single Reset notification.
    /// Callers removing multiple disjoint runs must go back-to-front so earlier indices stay valid.
    /// </summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= Items.Count) return;
        count = Math.Min(count, Items.Count - index);

        CheckReentrancy();

        _suppressNotification = true;
        try
        {
            if (Items is List<T> backing)
            {
                backing.RemoveRange(index, count);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    Items.RemoveAt(index);
                }
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(CountPropertyChanged);
        OnPropertyChanged(IndexerPropertyChanged);
        base.OnCollectionChanged(ResetEventArgs);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnCollectionChanged(e);
        }
    }
}

/// <summary>
/// 主窗口视图模型
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private sealed class PendingLogBatch
    {
        public required string PortName { get; init; }

        public required List<LogEntry> Logs { get; init; }
    }

    private sealed class PortSendResult
    {
        public required string PortName { get; init; }

        public bool IsSuccess { get; init; }

        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// 波特率检测建议事件
    /// </summary>
    public event EventHandler<BaudRateSuggestionEventArgs>? BaudRateSuggested;
    private readonly ISerialPortService _serialPortService;
    private readonly ITuningProtocolService _tuningProtocolService;
    private readonly IFileLoggerService _fileLoggerService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Services.IBaudRateDetectorService? _baudRateDetectorService;
    private readonly Services.IDataValidationService? _dataValidationService;

    [ObservableProperty]
    private string _title = $"串口工具 - Multi-Port Serial Monitor {VersionInfo.VersionString}";
    
    /// <summary>
    /// Gets the application version information
    /// </summary>
    public string AppVersion => VersionInfo.Version;
    
    /// <summary>
    /// Gets the build time
    /// </summary>
    public string BuildTime => VersionInfo.BuildTime;
    
    /// <summary>
    /// Gets the complete version string
    /// </summary>
    public string VersionDisplay => VersionInfo.VersionString;

    [ObservableProperty]
    private ObservableCollection<string> _availablePorts = new();

    [ObservableProperty]
    private ObservableCollection<PortViewModel> _openPorts = new();

    // Maintained from OpenPorts.CollectionChanged so we never do an O(n) LINQ scan over
    // OpenPorts on the hot data-receive path. Used for color lookup (per received chunk) and
    // for the per-flush sent-log / stats-refresh dictionary that used to be rebuilt with
    // OpenPorts.ToDictionary(...) on every UI flush.
    //
    // ConcurrentDictionary, not Dictionary: the writes happen on the UI thread
    // (OpenPorts.CollectionChanged) while GetPortColor / GetPortDisplayColor read it from each
    // port's serial read thread on every received chunk. A plain Dictionary being enumerated or
    // grown while another thread reads it can tear, throw, or hand back a corrupt bucket — the
    // reads are hot and completely unsynchronized, so the container itself has to be.
    private readonly ConcurrentDictionary<string, PortViewModel> _portsByName =
        new(StringComparer.OrdinalIgnoreCase);

    private void OnOpenPortsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                {
                    foreach (PortViewModel item in e.NewItems)
                    {
                        _portsByName[item.PortName] = item;
                    }
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems != null)
                {
                    foreach (PortViewModel item in e.OldItems)
                    {
                        _portsByName.TryRemove(item.PortName, out _);
                    }
                }
                break;
            case NotifyCollectionChangedAction.Replace:
                if (e.OldItems != null)
                {
                    foreach (PortViewModel item in e.OldItems)
                    {
                        _portsByName.TryRemove(item.PortName, out _);
                    }
                }
                if (e.NewItems != null)
                {
                    foreach (PortViewModel item in e.NewItems)
                    {
                        _portsByName[item.PortName] = item;
                    }
                }
                break;
            case NotifyCollectionChangedAction.Reset:
                // Clear() then re-add: a reader racing this window either misses the port (and
                // falls back to RxColorHex, same as a not-yet-open port) or sees the fresh entry.
                _portsByName.Clear();
                foreach (var p in OpenPorts)
                {
                    _portsByName[p.PortName] = p;
                }
                break;
        }

        // Cheap to maintain here and it keeps the status bar off a per-frame path.
        OpenPortCount = _portsByName.Count;

        // "全部关闭" is only enabled while at least one port is open.
        CloseAllPortsCommand.NotifyCanExecuteChanged();
    }

    // AllLogs is the unfiltered back-buffer used by FilterLogs() when SearchText changes. It is
    // never bound to the UI (only DisplayLogs is — see MainWindow.xaml). Holding it as an
    // ObservableCollection caused every flush to fire CollectionChanged / Count / Item[]
    // notifications for nothing. Plain List avoids that overhead.
    public List<LogEntry> AllLogs { get; } = new();

    [ObservableProperty]
    private RangeObservableCollection<LogEntry> _displayLogs = new();

    // NOTE: there used to be a `Filters` collection here, plus an injected ILogFilterService that was
    // forwarded to PortViewModel and immediately discarded (`_ = logFilterService;`). Nothing bound to
    // it and nothing subscribed to ILogFilterService.FiltersChanged, so the whole chain was dead code
    // that only obscured where filtering actually happens (it is the SearchText/GetOrCreateSearchRegex
    // path in FlushPendingLogBatches and FilterLogs). The service itself is still registered in DI for
    // a future rule-based filter UI; wiring it up is an explicit requirement, not an accident.

    [ObservableProperty]
    private ObservableCollection<string> _recentSearchTexts = new();

    private string _searchText = string.Empty;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // Validate regex pattern
                ValidateSearchPattern();

                // Update clear button visibility
                OnPropertyChanged(nameof(HasSearchText));

                // Debounce filter updates to reduce UI thrashing
                RequestFilterLogs();
            }
        }
    }

    /// <summary>
    /// Gets whether there is search text (for clear button visibility)
    /// </summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);
    
    /// <summary>
    /// Add a search text to recent history
    /// </summary>
    public void AddToRecentSearches(string searchText)
    {
        _logger.LogInformation("AddToRecentSearches called with: '{SearchText}'", searchText);

        if (string.IsNullOrWhiteSpace(searchText))
        {
            _logger.LogDebug("Search text is empty, skipping");
            return;
        }

        // Remove if already exists
        if (RecentSearchTexts.Contains(searchText))
        {
            _logger.LogDebug("Search text already exists, removing old entry");
            RecentSearchTexts.Remove(searchText);
        }

        // Add to beginning
        RecentSearchTexts.Insert(0, searchText);
        _logger.LogInformation("Added search text to history. Total count: {Count}", RecentSearchTexts.Count);

        // Keep only last 5
        while (RecentSearchTexts.Count > 5)
        {
            RecentSearchTexts.RemoveAt(RecentSearchTexts.Count - 1);
        }

        // Save to settings
        _logger.LogDebug("Saving recent searches to settings");
        _ = SaveRecentSearchesAsync();
    }
    
    /// <summary>
    /// Remove a search text from recent history
    /// </summary>
    public void RemoveFromRecentSearches(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return;
            
        if (RecentSearchTexts.Contains(searchText))
        {
            RecentSearchTexts.Remove(searchText);
            
            // Save to settings
            _ = SaveRecentSearchesAsync();
        }
    }
    
    /// <summary>
    /// Clear all recent searches
    /// </summary>
    public void ClearRecentSearches()
    {
        RecentSearchTexts.Clear();
        
        // Save to settings
        _ = SaveRecentSearchesAsync();
    }
    
    private async Task SaveRecentSearchesAsync()
    {
        try
        {
            var searchHistory = string.Join("|", RecentSearchTexts);
            _logger.LogInformation("Saving recent searches: '{SearchHistory}'", searchHistory);
            await _settingsService.SaveSettingAsync("RecentSearchTexts", searchHistory);
            _logger.LogInformation("Successfully saved recent searches");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save recent search texts");
        }
    }

    private async Task LoadRecentSearchesAsync()
    {
        try
        {
            _logger.LogInformation("Loading recent searches from settings");
            var searchHistory = await _settingsService.LoadSettingAsync("RecentSearchTexts", string.Empty);
            _logger.LogInformation("Loaded search history: '{SearchHistory}'", searchHistory);

            if (!string.IsNullOrEmpty(searchHistory))
            {
                var searches = searchHistory.Split('|', StringSplitOptions.RemoveEmptyEntries);
                _logger.LogInformation("Found {Count} search entries", searches.Length);

                RecentSearchTexts.Clear();
                foreach (var search in searches.Take(5))
                {
                    RecentSearchTexts.Add(search);
                    _logger.LogDebug("Added search to history: '{Search}'", search);
                }

                _logger.LogInformation("Loaded {Count} recent searches", RecentSearchTexts.Count);
            }
            else
            {
                _logger.LogInformation("No recent searches found in settings");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load recent search texts");
        }
    }
    
    private System.Threading.Timer? _filterDebounceTimer;

    // Compiled-regex cache for the live search filter. FlushPendingLogBatches runs ~20x/sec
    // while a search is active and FilterLogs runs on every debounced keystroke; both must
    // reuse the same compiled instance instead of paying milliseconds of RegexOptions.Compiled
    // codegen on the UI thread each time. Only touched on the UI thread (SearchText setter,
    // FilterLogs, flush timer), so no synchronization is needed.
    private string? _cachedSearchRegexPattern;
    private Regex? _cachedSearchRegex;

    [ObservableProperty]
    private bool _isRegexValid = true;

    [ObservableProperty]
    private string _regexErrorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountDisplay))]
    private int _matchCount = 0;

    /// <summary>Match count for the status bar; empty when nothing is being filtered.</summary>
    public string MatchCountDisplay => MatchCount > 0 ? $"匹配 {MatchCount} 条" : string.Empty;

    private void ValidateSearchPattern()
    {
        if (string.IsNullOrEmpty(SearchText))
        {
            IsRegexValid = true;
            RegexErrorMessage = string.Empty;
            return;
        }

        try
        {
            // Validation only parses the pattern; a non-compiled instance is cheap and the
            // compiled version is built once (and cached) by GetOrCreateSearchRegex when the
            // pattern is actually used for filtering.
            _ = new Regex(SearchText, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            IsRegexValid = true;
            RegexErrorMessage = string.Empty;
        }
        catch (ArgumentException ex)
        {
            IsRegexValid = false;
            RegexErrorMessage = $"Invalid regex: {ex.Message}";
            _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", SearchText);
        }
    }

    // IsPaused replaces the old AutoScroll concept. UX:
    //   IsPaused = false (default): log view is locked to the bottom; new data flows in and the
    //     view tracks the latest line. The user cannot meaningfully scroll while data is arriving
    //     (each new batch yanks the view back to the bottom).
    //   IsPaused = true: new data stops being added to the UI list (file logging continues
    //     independently). The user can scroll the existing logs freely. Resuming starts feeding
    //     new data again from that moment on — backlog accumulated during pause is not replayed
    //     to the UI; it's already in the log file on disk.
    [ObservableProperty]
    private bool _isPaused = false;

    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    partial void OnIsPausedChanged(bool value)
    {
        OnPropertyChanged(nameof(PauseButtonText));
    }

    [ObservableProperty]
    private bool _sendAsHex = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _sendText = string.Empty;

    [ObservableProperty]
    private bool _showSentData = true;

    [ObservableProperty]
    private string _txColorHex = PortColorPalette.DefaultTxHex;

    [ObservableProperty]
    private string _rxColorHex = PortColorPalette.DefaultRxHex;

    /// <summary>
    /// TX colour resolved for the active appearance. <see cref="TxColorHex"/> itself always stays a
    /// palette slot because it is persisted and rebound to <c>TxColorOptions</c>.
    /// </summary>
    private string TxColorHexResolved => PortColorPalette.Resolve(TxColorHex, _isDarkTheme);

    /// <summary>Settings key for <see cref="IsTuningEnabled"/>.</summary>
    private const string TuningEnabledSettingKey = "TuningEnabled";

    /// <summary>
    /// Master switch for the whole Tuning / TOTA feature. Defaults to <c>false</c>: the panel stays
    /// hidden and the automatic watch never resumes until the user opts in from 工具 → 启用 Tuning 功能.
    /// </summary>
    [ObservableProperty]
    private bool _isTuningEnabled = false;

    [ObservableProperty]
    private string _tuningBinFilePath = string.Empty;

    [ObservableProperty]
    private string _tuningDescriptorFilePath = string.Empty;

    [ObservableProperty]
    private bool _isTuningDescriptorValid = false;

    [ObservableProperty]
    private bool _isTuningWatching = false;

    [ObservableProperty]
    private bool _isTuningBusy = false;

    [ObservableProperty]
    private string _tuningStatus = "未配置 tuning";

    [ObservableProperty]
    private string _tuningDescriptorStatus = "未选择 JSON 描述文件";

    [ObservableProperty]
    private bool _canUseTuning = false;

    public bool HasTuningBinFilePath => !string.IsNullOrWhiteSpace(TuningBinFilePath);

    public bool HasTuningDescriptorPath => !string.IsNullOrWhiteSpace(TuningDescriptorFilePath);

    /// <summary>File name of the tuning payload, for the toolbar chip (full path is the tooltip).</summary>
    public string TuningBinDisplay => HasTuningBinFilePath ? Path.GetFileName(TuningBinFilePath) : string.Empty;

    /// <summary>File name of the protocol descriptor, for the toolbar chip.</summary>
    public string TuningDescriptorDisplay =>
        HasTuningDescriptorPath ? Path.GetFileName(TuningDescriptorFilePath) : string.Empty;

    public string TuningWatchButtonText => IsTuningWatching ? "停止监听" : "开始监听";

    private TuningProtocolDescriptor? _tuningDescriptor;
    private FileSystemWatcher? _tuningFileWatcher;
    private CancellationTokenSource? _tuningChangeCts;
    private readonly SemaphoreSlim _tuningSendLock = new(1, 1);
    private string _lastTuningBaselineHash = string.Empty;
    private bool _suppressTuningWatchPersistence = false;
    private bool _skipTuningEnabledPersistence = false;

    /// <summary>
    /// Gets the next unused port identity slot.
    /// </summary>
    /// <remarks>
    /// Returns the slot hex (the light value), which is both what gets persisted and what keeps the
    /// colour stable across an appearance change. Never return a resolved (dark) hex here — it would
    /// be written to settings.json and would no longer map back to a slot.
    /// </remarks>
    private string GetNextPortColor()
    {
        var usedColors = OpenPorts.Select(p => p.ColorHex).ToHashSet();
        foreach (var slot in PortColorPalette.Slots)
        {
            if (!usedColors.Contains(slot.SlotHex))
                return slot.SlotHex;
        }
        // 如果所有颜色都用完了，从头开始循环
        return PortColorPalette.Slots[OpenPorts.Count % PortColorPalette.Slots.Count].SlotHex;
    }

    /// <summary>
    /// 获取端口的保存颜色，如果没有保存过则分配新颜色
    /// </summary>
    private async Task<string> GetOrAssignPortColorAsync(string portName)
    {
        var savedColor = await _settingsService.LoadSettingAsync($"PortColor_{portName}", string.Empty);
        if (!string.IsNullOrEmpty(savedColor))
        {
            return savedColor;
        }
        return GetNextPortColor();
    }

    /// <summary>
    /// 保存端口颜色设置
    /// </summary>
    public void SavePortColor(string portName, string colorHex)
    {
        _ = _settingsService.SaveSettingAsync($"PortColor_{portName}", colorHex);
    }

    /// <summary>
    /// 获取指定端口的颜色槽位（持久化的值，不是渲染值）
    /// </summary>
    public string GetPortColor(string portName)
    {
        return _portsByName.TryGetValue(portName, out var port) ? port.ColorHex : RxColorHex;
    }

    /// <summary>
    /// Hex to actually render for a port under the active appearance.
    /// </summary>
    /// <remarks>
    /// Called once per received chunk (not per line) from the read path, so the cost is a single
    /// dictionary lookup on top of the existing lookup — no allocation, no LINQ.
    /// </remarks>
    public string GetPortDisplayColor(string portName)
    {
        return PortColorPalette.Resolve(GetPortColor(portName), _isDarkTheme);
    }

    #region Appearance

    private bool _isDarkTheme;
    private bool _skipThemePersistence;

    /// <summary>Settings key for <see cref="IsSidebarCollapsed"/>.</summary>
    private const string SidebarCollapsedSettingKey = "SidebarCollapsed";

    /// <summary>True when the currently applied appearance resolves to the dark palette.</summary>
    public bool IsDarkTheme => _isDarkTheme;

    /// <summary>
    /// User's appearance choice. Persisted by <see cref="OnThemePreferenceChanged"/>; the actual
    /// application to the visual tree is done by <c>MainWindow</c>, which owns the root element.
    /// </summary>
    [ObservableProperty]
    private AppThemePreference _themePreference = AppThemePreference.System;

    /// <summary>Human-readable appearance for the status bar.</summary>
    [ObservableProperty]
    private string _appearanceDisplay = "跟随系统";

    /// <summary>
    /// Adopts the preference read from disk at startup <em>without</em> writing it back.
    /// </summary>
    public void InitializeThemePreference(AppThemePreference preference)
    {
        _skipThemePersistence = true;
        try
        {
            ThemePreference = preference;
        }
        finally
        {
            _skipThemePersistence = false;
        }
    }

    partial void OnThemePreferenceChanged(AppThemePreference value)
    {
        AppearanceDisplay = value switch
        {
            AppThemePreference.Light => "浅色",
            AppThemePreference.Dark => "深色",
            _ => "跟随系统"
        };

        if (!_skipThemePersistence)
        {
            _ = _settingsService.SaveSettingAsync(App.ThemeSettingKey, value.ToString());
        }
    }

    /// <summary>
    /// Re-derives every colour that is stored as a palette slot into the variant for the active
    /// appearance, including rows that are already on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <c>MainWindow</c> after the root element's theme (and therefore
    /// <c>ActualTheme</c>) has settled. The sweep over <see cref="AllLogs"/> is O(n) with n bounded by
    /// <c>AllLogsTrimThreshold</c>, runs only on a user action, and never replaces the
    /// <see cref="DisplayLogs"/> instance — the "never swap the bound collection" rule still holds.
    /// </para>
    /// <para>
    /// This is why <c>Controls/LogListView.xaml</c> binds <c>ColorHex</c> with <c>Mode=OneWay</c>:
    /// a compiled <c>x:Bind</c> defaults to OneTime, so without it the rows would keep the previous
    /// theme's brush until the list was rebuilt.
    /// </para>
    /// </remarks>
    public void ApplyEffectiveTheme(bool isDark)
    {
        if (_isDarkTheme == isDark)
        {
            return;
        }

        _isDarkTheme = isDark;
        OnPropertyChanged(nameof(IsDarkTheme));

        foreach (var port in OpenPorts)
        {
            port.RefreshDisplayColor(isDark);
        }

        foreach (var entry in AllLogs)
        {
            entry.ColorHex = PortColorPalette.Resolve(entry.ColorHex, isDark);
        }

        foreach (var option in TxColorOptions)
        {
            option.RefreshBrush(isDark);
        }
    }

    /// <summary>
    /// Re-colours the log rows already on screen for one port after its colour was changed.
    /// </summary>
    /// <remarks>
    /// Without this the sidebar swatch and the channel legend follow the new colour while the log rows
    /// keep the old brush until they are recycled or the list is rebuilt — the same "two palettes on
    /// screen" failure the appearance sweep exists to prevent, only triggered by a per-port change.
    /// Only received rows are touched: sent/tuning rows carry the TX colour, not the port colour.
    /// </remarks>
    public void ApplyPortColorChange(string portName, string colorHex)
    {
        var resolved = PortColorPalette.Resolve(colorHex, _isDarkTheme);

        foreach (var entry in AllLogs)
        {
            if (entry.IsReceived &&
                string.Equals(entry.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                entry.ColorHex = resolved;
            }
        }
    }

    /// <summary>Swatch options for the TX colour picker; brushes follow the active appearance.</summary>
    public ObservableCollection<PortColorOption> TxColorOptions { get; } = BuildTxColorOptions();

    private static ObservableCollection<PortColorOption> BuildTxColorOptions()
    {
        var options = new ObservableCollection<PortColorOption>();
        foreach (var slot in PortColorPalette.TxOptions)
        {
            options.Add(new PortColorOption(slot));
        }
        return options;
    }

    #region Sidebar & status bar

    /// <summary>Whether the user folded the port configuration rail away.</summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync(SidebarCollapsedSettingKey, value ? 1 : 0);
    }

    /// <summary>Number of currently open ports, maintained on the UI thread by the collection hook.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenPortCountDisplay))]
    [NotifyPropertyChangedFor(nameof(HasOpenPorts))]
    private int _openPortCount;

    /// <summary>Open-port count for the status bar.</summary>
    public string OpenPortCountDisplay => OpenPortCount == 0 ? "未打开串口" : $"已打开 {OpenPortCount} 个串口";

    /// <summary>Drives the collapse of the channel legend strip when there is nothing to legend.</summary>
    public bool HasOpenPorts => OpenPortCount > 0;

    /// <summary>Combined RX/TX totals across every open port, refreshed on the throttled stats tick.</summary>
    [ObservableProperty]
    private string _totalTrafficDisplay = "↓ 0 B  ↑ 0 B";

    /// <summary>
    /// Sums the cumulative counters of all open ports.
    /// </summary>
    /// <remarks>
    /// Only ever called from inside the existing ~4 Hz stats-refresh window in
    /// <c>FlushPendingLogBatches</c> — deliberately not per batch and never per byte, because that
    /// is exactly the kind of per-packet work the throttling exists to prevent.
    /// </remarks>
    private void RefreshTrafficTotals()
    {
        long received = 0;
        long sent = 0;

        foreach (var port in _portsByName.Values)
        {
            var stats = _serialPortService.GetStatistics(port.PortName);
            received += stats.ReceivedBytes;
            sent += stats.SentBytes;
        }

        TotalTrafficDisplay = $"↓ {FormatDataSize(received)}  ↑ {FormatDataSize(sent)}";
    }

    #endregion

    #endregion

    partial void OnSendAsHexChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync("SendAsHex", value ? 1 : 0);
    }

    partial void OnSendTextChanged(string value)
    {
        _ = _settingsService.SaveSettingAsync("SendText", value);
    }

    partial void OnShowSentDataChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync("ShowSentData", value ? 1 : 0);
    }

    partial void OnTxColorHexChanged(string value)
    {
        _ = _settingsService.SaveSettingAsync("TxColorHex", value);
    }

    partial void OnRxColorHexChanged(string value)
    {
        _ = _settingsService.SaveSettingAsync("RxColorHex", value);
    }

    partial void OnTuningBinFilePathChanged(string value)
    {
        _ = _settingsService.SaveSettingAsync("TuningBinFilePath", value);
        OnPropertyChanged(nameof(HasTuningBinFilePath));
        OnPropertyChanged(nameof(TuningBinDisplay));
        RefreshTuningAvailability();
    }

    partial void OnTuningDescriptorFilePathChanged(string value)
    {
        _ = _settingsService.SaveSettingAsync("TuningDescriptorFilePath", value);
        OnPropertyChanged(nameof(HasTuningDescriptorPath));
        OnPropertyChanged(nameof(TuningDescriptorDisplay));
        RefreshTuningAvailability();
    }

    partial void OnIsTuningDescriptorValidChanged(bool value)
    {
        RefreshTuningAvailability();
    }

    partial void OnIsTuningWatchingChanged(bool value)
    {
        if (!_suppressTuningWatchPersistence)
        {
            _ = _settingsService.SaveSettingAsync("TuningIsWatching", value ? 1 : 0);
        }

        OnPropertyChanged(nameof(TuningWatchButtonText));
    }

    partial void OnIsTuningEnabledChanged(bool value)
    {
        if (!_skipTuningEnabledPersistence)
        {
            _ = _settingsService.SaveSettingAsync(TuningEnabledSettingKey, value ? 1 : 0);
        }

        // Recompute first: both branches below depend on an up-to-date CanUseTuning.
        RefreshTuningAvailability();

        if (!value && IsTuningWatching)
        {
            // Hide the feature but keep every saved tuning setting, so re-enabling it restores exactly
            // what the user had. persistState: false is what preserves the "was watching" preference.
            StopTuningWatch(persistState: false);
        }

        if (!value)
        {
            TuningStatus = "Tuning 功能未启用";
        }

        if (_skipTuningEnabledPersistence)
        {
            // Startup read: no status message and no resume here — InitializeAsync owns the resume,
            // after the tuning paths and the listening preference have both been read.
            return;
        }

        StatusMessage = value ? "已启用 Tuning 功能" : "已关闭 Tuning 功能";

        if (value)
        {
            _ = ResumeTuningWatchIfPreferredAsync();
        }
    }

    /// <summary>
    /// Adopts the master switch read from disk at startup <em>without</em> writing it back, emitting a
    /// status message, or resuming the watch.
    /// </summary>
    public void InitializeTuningEnabled(bool enabled)
    {
        _skipTuningEnabledPersistence = true;
        try
        {
            IsTuningEnabled = enabled;
        }
        finally
        {
            _skipTuningEnabledPersistence = false;
        }
    }

    /// <summary>
    /// Restores the saved "was watching" preference after the user re-enables the feature.
    /// </summary>
    private async Task ResumeTuningWatchIfPreferredAsync()
    {
        if (!IsTuningEnabled || IsTuningWatching || !CanUseTuning)
        {
            return;
        }

        if (await _settingsService.LoadSettingAsync("TuningIsWatching", 0) != 1)
        {
            return;
        }

        await StartTuningWatchAsync(createBaseline: true);
    }

    private const int MaxDisplayLogs = 2000; // Increased limit with optimizations
    private const int DisplayLogTrimThreshold = MaxDisplayLogs + 200;
    // Trimming removes this much MORE than the overflow, so the next trim is ~600 entries away
    // instead of one flush away. Each trim publishes a Reset (a full view rebuild), so the headroom
    // is what turns "rebuild 20x/sec forever once the cap is hit" into "rebuild every few seconds"
    // — the steady-state list oscillates between MaxDisplayLogs - DisplayLogTrimHeadroom and the
    // threshold rather than sitting pinned at the cap. See RemoveFromStart for why a multi-item
    // Remove notification is not used instead.
    private const int DisplayLogTrimHeadroom = 400;
    private const int AllLogsTrimThreshold = MaxDisplayLogs * 2 + 400;
    private const int MaxQueuedLogEntries = MaxDisplayLogs * 4;
    // Smaller batches at a fixed cadence give shorter, more uniform UI blocks. Bigger batches feel
    // like noticeable hitches. With 100 items at 50ms cadence we can sustain ~2000 lines/sec while
    // keeping each UI flush short enough that the user's wheel/drag input stays responsive.
    private const int MaxUiLogEntriesPerFlush = 100;
    private const int FlushIntervalMs = 50;
    private const int ErrorStatusThrottleMs = 250;
    // Port stat counters don't need 20Hz UI updates; ~4Hz is visually identical and skips
    // most per-flush GetStatistics/UpdateStatistics work.
    private const int StatsRefreshIntervalMs = 250;

    private DispatcherQueueTimer? _flushTimer;
    private long _lastErrorStatusTicks;
    // Ports whose stats are due for a UI refresh. Only touched on the UI thread
    // (FlushPendingLogBatches), so no synchronization needed.
    private readonly HashSet<string> _pendingStatsPorts = new(StringComparer.OrdinalIgnoreCase);
    private long _lastStatsRefreshTick;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isScanning = false;

    // Port configuration properties
    [ObservableProperty]
    private int _baudRate = 3000000; // Default to 3M

    [ObservableProperty]
    private int _dataBits = 8;

    [ObservableProperty]
    private System.IO.Ports.StopBits _stopBits = System.IO.Ports.StopBits.One;

    [ObservableProperty]
    private System.IO.Ports.Parity _parity = System.IO.Ports.Parity.None;

    public ObservableCollection<int> AvailableBaudRates { get; } = new()
    {
        1152000, 2000000, 3000000, 6000000
    };

    [ObservableProperty]
    private string _customBaudRate = string.Empty;

    [ObservableProperty]
    private bool _useCustomBaudRate = false;

    /// <summary>
    /// Upper bound accepted for a custom baud rate.
    /// </summary>
    /// <remarks>
    /// Matches the highest rate <see cref="Services.IBaudRateDetectorService"/> probes. SerialPort
    /// itself only requires a positive value, so a typo like 300000000 was previously accepted and
    /// produced a port that opened and then delivered nothing — indistinguishable from a wiring fault.
    /// </remarks>
    private const int MaxSupportedBaudRate = 12_000_000;

    /// <summary>
    /// Resolves the baud rate to open with, validating the custom value once for both call sites.
    /// </summary>
    /// <remarks>
    /// The single/collective open paths used to carry byte-for-byte copies of this check, and both
    /// only tested <c>&gt; 0</c>.
    /// </remarks>
    private bool TryResolveBaudRate(out int baudRate, out string? errorMessage)
    {
        baudRate = BaudRate;
        errorMessage = null;

        if (!UseCustomBaudRate || string.IsNullOrWhiteSpace(CustomBaudRate))
        {
            return true;
        }

        if (!int.TryParse(
                CustomBaudRate.Trim(),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var customRate) ||
            customRate <= 0 ||
            customRate > MaxSupportedBaudRate)
        {
            errorMessage = $"自定义波特率无效：请输入 1 ~ {MaxSupportedBaudRate} 之间的整数";
            _logger.LogWarning("Rejected custom baud rate input: '{CustomBaudRate}'", CustomBaudRate);
            return false;
        }

        baudRate = customRate;
        return true;
    }

    /// <summary>
    /// Format bytes into human-readable units (B, KB, MB)
    /// </summary>
    public static string FormatDataSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        else if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F2} KB";
        else
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    public ObservableCollection<int> AvailableDataBits { get; } = new()
    {
        5, 6, 7, 8
    };

    public MainViewModel(
        ISerialPortService serialPortService,
        ITuningProtocolService tuningProtocolService,
        IFileLoggerService fileLoggerService,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger,
        Services.IBaudRateDetectorService? baudRateDetectorService = null,
        Services.IDataValidationService? dataValidationService = null)
    {
        _serialPortService = serialPortService;
        _tuningProtocolService = tuningProtocolService;
        _fileLoggerService = fileLoggerService;
        _settingsService = settingsService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _baudRateDetectorService = baudRateDetectorService;
        _dataValidationService = dataValidationService;

        // Keep _portsByName in sync with OpenPorts so the hot data-receive path can do O(1)
        // lookups instead of LINQ scans. OpenPorts itself is the source of truth for the UI.
        OpenPorts.CollectionChanged += OnOpenPortsChanged;

        // Subscribe to events
        _serialPortService.DataReceived += OnDataReceived;
        _serialPortService.PortStateChanged += OnPortStateChanged;
        _serialPortService.ErrorOccurred += OnErrorOccurred;

        // The settings service refuses to write when the existing settings.json could not be parsed
        // (writing would replace the user's real configuration with an empty document). That state is
        // invisible otherwise, so surface it.
        _settingsService.SettingsLoadFailed += OnSettingsLoadFailed;
        
        // Subscribe to baud rate detection requests
        if (_serialPortService is SerialPortService serialPortServiceInstance)
        {
            serialPortServiceInstance.BaudRateDetectionRequested += OnBaudRateDetectionRequested;
        }

        // Initialize
        _ = InitializeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogError(t.Exception, "Failed to initialize MainViewModel");
        }, TaskScheduler.Default);
    }

    private async Task InitializeAsync()
    {
        // Load saved baud rate settings
        BaudRate = await _settingsService.LoadSettingAsync("BaudRate", 3000000);
        UseCustomBaudRate = await _settingsService.LoadSettingAsync("UseCustomBaudRate", 0) == 1;
        CustomBaudRate = await _settingsService.LoadSettingAsync("CustomBaudRate", string.Empty);
        _logger.LogInformation("Loaded baud rate settings: BaudRate={BaudRate}, UseCustom={UseCustom}, CustomValue={CustomValue}",
            BaudRate, UseCustomBaudRate, CustomBaudRate);

        // Load send settings
        SendAsHex = await _settingsService.LoadSettingAsync("SendAsHex", 0) == 1;
        SendText = await _settingsService.LoadSettingAsync("SendText", string.Empty);
        ShowSentData = await _settingsService.LoadSettingAsync("ShowSentData", 1) == 1;
        TxColorHex = await _settingsService.LoadSettingAsync("TxColorHex", PortColorPalette.DefaultTxHex);
        RxColorHex = await _settingsService.LoadSettingAsync("RxColorHex", PortColorPalette.DefaultRxHex);
        IsSidebarCollapsed = await _settingsService.LoadSettingAsync(SidebarCollapsedSettingKey, 0) == 1;

        // Load tuning settings. The master switch is read first: the paths and the listening preference
        // are still loaded (they are preserved across a disable), but nothing may start listening while
        // the feature is off. The protocol itself is never defaulted; it must come from the user's JSON.
        InitializeTuningEnabled(await _settingsService.LoadSettingAsync(TuningEnabledSettingKey, 0) == 1);
        TuningBinFilePath = await _settingsService.LoadSettingAsync("TuningBinFilePath", string.Empty);
        TuningDescriptorFilePath = await _settingsService.LoadSettingAsync("TuningDescriptorFilePath", string.Empty);
        _lastTuningBaselineHash = await _settingsService.LoadSettingAsync("TuningBaselineHash", string.Empty);
        if (!string.IsNullOrWhiteSpace(TuningDescriptorFilePath))
        {
            await LoadTuningDescriptorCoreAsync();
        }
        else
        {
            RefreshTuningAvailability();
        }

        // Load recent search texts
        await LoadRecentSearchesAsync();

        // Scan ports
        await ScanPortsAsync();

        var shouldResumeTuningWatch = await _settingsService.LoadSettingAsync("TuningIsWatching", 0) == 1;
        if (IsTuningEnabled && shouldResumeTuningWatch && CanUseTuning)
        {
            await StartTuningWatchAsync(createBaseline: true);
        }

        // The failure event may have fired before this ViewModel existed (App.OnLaunched reads the
        // saved appearance first), so check the state as well as subscribing to the event.
        if (_settingsService.HasLoadFailure)
        {
            ReportSettingsLoadFailure();
        }
    }

    private const string SettingsLoadFailureMessage =
        "设置文件读取失败：为避免覆盖原有配置，本次运行不会写回 settings.json。请修复或删除该文件后重启。";

    private void OnSettingsLoadFailed(object? sender, EventArgs e) => ReportSettingsLoadFailure();

    /// <remarks>
    /// Must not call back into <c>ISettingsService</c>: the event is raised while its file lock is
    /// held, and the lock is not reentrant. Setting a property is all this may do.
    /// </remarks>
    private void ReportSettingsLoadFailure()
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            StatusMessage = SettingsLoadFailureMessage;
            return;
        }

        _dispatcherQueue.TryEnqueue(() => StatusMessage = SettingsLoadFailureMessage);
    }

    [RelayCommand(CanExecute = nameof(CanScanPorts))]
    private async Task ScanPortsAsync()
    {
        IsScanning = true;
        StatusMessage = "Scanning ports...";

        try
        {
            var ports = await _serialPortService.GetAvailablePortsAsync();
            AvailablePorts.Clear();
            foreach (var port in ports)
            {
                AvailablePorts.Add(port);
            }

            StatusMessage = $"Found {AvailablePorts.Count} available ports";
            _logger.LogInformation("Port scan completed, found {Count} ports", AvailablePorts.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error scanning ports: {ex.Message}";
            _logger.LogError(ex, "Error scanning ports");
        }
        finally
        {
            IsScanning = false;
            // "Open all" is only meaningful once a scan has produced a list.
            OpenAllPortsCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Guards the scan against re-entry while one is in flight.</summary>
    private bool CanScanPorts() => !ScanPortsCommand.IsRunning;

    // Ports with an open request in flight. Note OpenPortCommand.ExecuteAsync is also called directly
    // from MainWindow's port-list SelectionChanged, which bypasses CanExecute entirely — the
    // `_portsByName` check alone cannot stop a double-open because the two calls interleave across the
    // awaits below, and the second one used to add a second entry for the same port name.
    private readonly ConcurrentDictionary<string, byte> _openingPorts = new(StringComparer.OrdinalIgnoreCase);

    // Bumped by every "close all" request (CloseAllPortsAsync).
    //
    // An open request spends up to a few seconds inside SerialPortService (availability probe + up to
    // three open attempts with 500 ms backoff), and a 全部关闭 pressed in that window had nothing to
    // close: the port is not in the service's map yet and has no row. The close-all therefore
    // completed "successfully", and seconds later the port appeared — open. Sampled before an open
    // starts and compared after it returns, so an open that began before the close-all undoes itself,
    // while one started afterwards is unaffected.
    private int _closeAllEpoch;

    [RelayCommand]
    private async Task OpenPortAsync(string portName)
    {
        if (string.IsNullOrEmpty(portName))
        {
            StatusMessage = "Please select a port";
            return;
        }

        if (_portsByName.ContainsKey(portName))
        {
            StatusMessage = $"Port {portName} is already open";
            return;
        }

        // Atomic claim: only the first caller for this port name proceeds past this point.
        if (!_openingPorts.TryAdd(portName, 0))
        {
            StatusMessage = $"Port {portName} 正在打开中，请稍候";
            _logger.LogInformation("Ignored a concurrent open request for port {PortName}", portName);
            return;
        }

        try
        {
            // Determine which baud rate to use
            if (!TryResolveBaudRate(out var baudRateToUse, out var baudRateError))
            {
                StatusMessage = baudRateError!;
                return;
            }

            _logger.LogInformation("Opening {PortName} with baud rate {BaudRate}", portName, baudRateToUse);

            var config = new SerialPortConfig
            {
                PortName = portName,
                BaudRate = baudRateToUse,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity
            };

            // Sampled before the open and compared after it: 全部关闭 pressed while this port was
            // still being opened must not be undone by the port arriving a second later.
            var closeAllEpoch = Volatile.Read(ref _closeAllEpoch);

            var opened = await _serialPortService.OpenPortAsync(config);
            if (opened)
            {
                if (Volatile.Read(ref _closeAllEpoch) != closeAllEpoch)
                {
                    _logger.LogInformation(
                        "Port {PortName} finished opening after a close-all request; closing it again",
                        portName);
                    await _serialPortService.ClosePortAsync(portName);
                    StatusMessage = $"Port {portName} closed";
                    return;
                }

                // Start file logging
                await _fileLoggerService.StartLoggingAsync(portName);

                // 分配端口颜色（优先使用保存的颜色）
                var portColor = await GetOrAssignPortColorAsync(portName);
                var portViewModel = new PortViewModel(portName, _serialPortService, _dispatcherQueue)
                {
                    ColorHex = portColor
                };

                // Initialize statistics display
                var stats = _serialPortService.GetStatistics(portName);
                portViewModel.UpdateStatistics(stats);

                OpenPorts.Add(portViewModel);

                // Save port color and baud rate settings for next time
                SavePortColor(portName, portColor);
                await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
                await _settingsService.SaveSettingAsync("UseCustomBaudRate", UseCustomBaudRate ? 1 : 0);
                await _settingsService.SaveSettingAsync("CustomBaudRate", CustomBaudRate);
                _logger.LogInformation("Saved baud rate settings: UseCustom={UseCustom}, CustomValue={CustomValue}",
                    UseCustomBaudRate, CustomBaudRate);

                StatusMessage = $"Port {portName} opened successfully (BaudRate: {baudRateToUse}). Log file: {_fileLoggerService.GetLogFilePath(portName)}";
                _logger.LogInformation("Port {PortName} opened with baud rate {BaudRate}", portName, baudRateToUse);
            }
            else
            {
                StatusMessage = $"Failed to open port {portName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening port {portName}: {ex.Message}";
            _logger.LogError(ex, "Error opening port {PortName}", portName);
        }
        finally
        {
            // Always release the claim, including on the early `return`s above — otherwise a failed
            // open would make that port permanently un-openable for the rest of the session.
            _openingPorts.TryRemove(portName, out _);
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenAllPorts))]
    private async Task OpenAllPortsAsync()
    {
        if (AvailablePorts.Count == 0)
        {
            StatusMessage = "No available ports to open";
            return;
        }

        try
        {
            // Determine which baud rate to use
            if (!TryResolveBaudRate(out var baudRateToUse, out var baudRateError))
            {
                StatusMessage = baudRateError!;
                return;
            }

            _logger.LogInformation("OpenAllPorts: Using baud rate {BaudRate}", baudRateToUse);

            var defaultConfig = new SerialPortConfig
            {
                BaudRate = baudRateToUse,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity
            };

            // Sampled before the batch and compared after it: 全部关闭 pressed while this loop was
            // still opening ports must not be undone by those ports arriving afterwards.
            var closeAllEpoch = Volatile.Read(ref _closeAllEpoch);

            // What was already open before the batch, so the rollback below touches only the ports
            // this batch brought up.
            var alreadyOpen = _serialPortService.GetOpenPorts()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var openedCount = await _serialPortService.OpenAllPortsAsync(defaultConfig);

            if (Volatile.Read(ref _closeAllEpoch) != closeAllEpoch)
            {
                // Close exactly the ports this batch opened, not every open port: a port the user
                // opened by hand after pressing 关闭全部 is not ours to close. No rows are added
                // either, and OpenPorts is left alone — the concurrent 全部关闭 already cleared it.
                foreach (var portName in _serialPortService.GetOpenPorts())
                {
                    if (!alreadyOpen.Contains(portName))
                    {
                        await _serialPortService.ClosePortAsync(portName);
                    }
                }

                _logger.LogInformation(
                    "Batch open finished after a close-all request; rolled back the ports it opened");
                StatusMessage = "Ports closed";
                return;
            }

            // Start file logging and create ViewModels for each opened port
            foreach (var portName in _serialPortService.GetOpenPorts())
            {
                if (!_portsByName.ContainsKey(portName))
                {
                    await _fileLoggerService.StartLoggingAsync(portName);

                    // 分配端口颜色（优先使用保存的颜色）
                    var portColor = await GetOrAssignPortColorAsync(portName);
                    var portViewModel = new PortViewModel(portName, _serialPortService, _dispatcherQueue)
                    {
                        ColorHex = portColor
                    };
                    var stats = _serialPortService.GetStatistics(portName);
                    portViewModel.UpdateStatistics(stats);
                    OpenPorts.Add(portViewModel);

                    // 保存端口颜色
                    SavePortColor(portName, portColor);
                }
            }

            StatusMessage = $"Opened {openedCount} port(s) successfully (BaudRate: {baudRateToUse})";
            _logger.LogInformation("Batch opened {Count} ports with baud rate {BaudRate}", openedCount, baudRateToUse);

            // Save baud rate settings for next time
            await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
            await _settingsService.SaveSettingAsync("UseCustomBaudRate", UseCustomBaudRate ? 1 : 0);
            await _settingsService.SaveSettingAsync("CustomBaudRate", CustomBaudRate);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening ports: {ex.Message}";
            _logger.LogError(ex, "Error during batch port open");
        }
    }

    /// <summary>"全部打开" needs a scanned list and no batch open already in flight.</summary>
    private bool CanOpenAllPorts() => !OpenAllPortsCommand.IsRunning && AvailablePorts.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCloseAllPorts))]
    private async Task CloseAllPortsAsync()
    {
        // Bumped before anything else, including the "nothing to close" return, and also when the
        // request is rejected for being a duplicate: any open that is already in flight must notice
        // that a close-all happened, or it will finish and re-add a port the user just closed.
        Interlocked.Increment(ref _closeAllEpoch);

        if (OpenPorts.Count == 0)
        {
            StatusMessage = "No open ports to close";
            return;
        }

        try
        {
            var portsToClose = OpenPorts.ToList();
            var portCount = portsToClose.Count;
            StatusMessage = $"Closing {portCount} port(s)...";

            // Cancel any baud-rate scan first — a scan reopens the port when it ends, which would undo
            // this close for a port the user can no longer see (see CancelBaudRateDetection).
            foreach (var port in portsToClose)
            {
                CancelBaudRateDetection(port.PortName);
                _lineAssemblers.TryRemove(port.PortName, out _);
            }

            // Close the ports before stopping their file loggers. Stopping a logger awaits its writer
            // (see FileLoggerService), and the ports are what the user asked to close — bookkeeping
            // must not be able to sit in front of that. Closing first also captures the tail of the
            // log rather than truncating it.
            await _serialPortService.CloseAllPortsAsync();

            foreach (var port in portsToClose)
            {
                await _fileLoggerService.StopLoggingAsync(port.PortName);
            }

            // Clear the OpenPorts collection
            OpenPorts.Clear();

            StatusMessage = $"✅ Closed {portCount} port(s) successfully. Wait 1-2 seconds before reopening.";
            _logger.LogInformation("Batch closed {Count} ports", portCount);
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error closing ports: {ex.Message}. If ports won't reopen, restart the application.";
            _logger.LogError(ex, "Error during batch port close");
        }
    }

    /// <summary>"全部关闭" needs at least one open port and no batch close already in flight.</summary>
    private bool CanCloseAllPorts() => !CloseAllPortsCommand.IsRunning && OpenPorts.Count > 0;

    /// <summary>Debounce window for live search re-filtering.</summary>
    private const int FilterDebounceMs = 150;

    /// <summary>
    /// Requests a re-filter on the shared 150 ms debounce.
    /// </summary>
    /// <remarks>
    /// This is the ONLY entry point callers outside this class should use. FilterLogs is an O(n) full
    /// rebuild of DisplayLogs (two HashSet passes plus the regex sweep over AllLogs, ~2000 entries), and
    /// it used to be invoked synchronously from three separate UI handlers — the search dropdown
    /// selection, the dropdown closing, and the Enter key — each of which had already assigned
    /// <see cref="SearchText"/> and therefore already armed the debounce. The result was up to four
    /// full rebuilds per keystroke or selection, which is exactly what the debounce exists to prevent.
    /// </remarks>
    public void RequestFilterLogs()
    {
        if (_disposed)
        {
            return;
        }

        var timer = _filterDebounceTimer;
        if (timer == null)
        {
            // Created once and re-armed with Change(). Creating (and disposing) a Timer per call meant a
            // fresh timer — and its internal wait-handle bookkeeping — on every keystroke in the search
            // box.
            timer = new System.Threading.Timer(
                _ => _dispatcherQueue.TryEnqueue(FilterLogs),
                null,
                System.Threading.Timeout.Infinite,
                System.Threading.Timeout.Infinite);
            _filterDebounceTimer = timer;
        }

        try
        {
            timer.Change(FilterDebounceMs, System.Threading.Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Racing Dispose(): the window is going away, nothing to re-arm.
        }
    }

    private void FilterLogs()
    {
        // CRITICAL: Never replace DisplayLogs collection, only modify in place
        // This prevents ListView from rebinding and causing flicker
        
        List<LogEntry> filtered;
        
        if (string.IsNullOrEmpty(SearchText))
        {
            filtered = AllLogs.ToList();
            MatchCount = 0;
        }
        else if (!IsRegexValid)
        {
            // If regex is invalid, show no results
            filtered = new List<LogEntry>();
            MatchCount = 0;
        }
        else
        {
            try
            {
                var regex = GetOrCreateSearchRegex(SearchText, IsRegexValid);
                if (regex != null)
                {
                    filtered = AllLogs.Where(log =>
                        regex.IsMatch(log.Content) ||
                        regex.IsMatch(log.PortName))
                        .ToList();
                }
                else
                {
                    filtered = new List<LogEntry>();
                }

                MatchCount = filtered.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying regex filter: {Pattern}", SearchText);
                filtered = new List<LogEntry>();
                MatchCount = 0;
            }
        }

        var filteredSet = new HashSet<LogEntry>(filtered);

        // Remove items that no longer match filter as contiguous runs (back-to-front so
        // earlier indices stay valid). Per-item RemoveAt moved the whole list tail once
        // per removal — O(n²) on 2000 items during search; run removal is one shift per
        // block and one Reset notification per block.
        int runStart = -1;
        for (int i = DisplayLogs.Count - 1; i >= -1; i--)
        {
            var shouldRemove = i >= 0 && !filteredSet.Contains(DisplayLogs[i]);
            if (shouldRemove && runStart < 0)
            {
                runStart = i;
            }
            else if (!shouldRemove && runStart >= 0)
            {
                DisplayLogs.RemoveRange(i + 1, runStart - i);
                runStart = -1;
            }
        }

        // Add new items that match filter but aren't in DisplayLogs yet (batch to reduce UI notifications)
        var displaySet = new HashSet<LogEntry>(DisplayLogs);
        var toAdd = new List<LogEntry>();
        foreach (var log in filtered)
        {
            if (!displaySet.Contains(log))
            {
                toAdd.Add(log);
            }
        }
        if (toAdd.Count > 0)
        {
            DisplayLogs.AddRange(toAdd);
        }
    }

    /// <summary>Non-whitespace separators accepted inside a hex payload.</summary>
    private static readonly char[] HexSeparators = { '-', '_', ',', ':', ';', '|' };

    /// <summary>
    /// Parses a hex payload into bytes, accepting whitespace- or separator-delimited groups and an
    /// optional <c>0x</c> prefix per group.
    /// </summary>
    /// <remarks>
    /// The previous implementation ran a global <c>Replace("0x", "").Replace("0X", "")</c> over the
    /// whole input, which corrupts any payload that legitimately contains those two characters:
    /// <c>A0x0B</c> became <c>A0B</c> and was either rejected as "odd length" or, when the surrounding
    /// digits happened to line up, silently sent as the wrong bytes. <c>0x</c> is a per-group notation,
    /// so it is only stripped at the start of a group here.
    /// </remarks>
    private static bool TryParseHexInput(string input, out byte[] bytes, out string? errorMessage)
    {
        bytes = Array.Empty<byte>();
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "十六进制内容为空";
            return false;
        }

        // Split on all whitespace first (a null char[] means "any whitespace"), then on the explicit
        // separators, so "A1-B2", "A1 B2" and "A1 - B2" all yield the same two groups.
        var tokens = input
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split(HexSeparators, StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        var digitsBuilder = new StringBuilder(input.Length);
        foreach (var token in tokens)
        {
            var digits = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;

            foreach (var c in digits)
            {
                if (!Uri.IsHexDigit(c))
                {
                    errorMessage = $"十六进制格式错误：无效字符 '{c}'";
                    return false;
                }
            }

            digitsBuilder.Append(digits);
        }

        if (digitsBuilder.Length == 0)
        {
            errorMessage = "十六进制内容为空";
            return false;
        }

        if (digitsBuilder.Length % 2 != 0)
        {
            errorMessage = "十六进制格式错误：长度必须为偶数";
            return false;
        }

        var parsed = new byte[digitsBuilder.Length / 2];
        for (var i = 0; i < parsed.Length; i++)
        {
            var pair = digitsBuilder.ToString(i * 2, 2);
            if (!byte.TryParse(
                    pair,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed[i]))
            {
                errorMessage = $"十六进制格式错误：无效字节 '{pair}'";
                return false;
            }
        }

        bytes = parsed;
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrEmpty(SendText))
            return;

        var targetPorts = OpenPorts
            .Select(port => port.PortName)
            .ToList();
        if (targetPorts.Count == 0)
        {
            StatusMessage = "请先打开一个串口";
            return;
        }

        try
        {
            string displayContent;
            byte[] data;

            if (SendAsHex)
            {
                if (!TryParseHexInput(SendText, out var bytes, out var hexError))
                {
                    StatusMessage = hexError!;
                    return;
                }

                data = bytes;
                displayContent = $"[HEX] {BitConverter.ToString(bytes).Replace("-", " ")}";
            }
            else
            {
                data = Encoding.UTF8.GetBytes(SendText);
                displayContent = SendText;
            }

            StatusMessage = targetPorts.Count == 1
                ? $"正在发送 {data.Length} 字节..."
                : $"正在向 {targetPorts.Count} 个串口发送 {data.Length} 字节...";

            var sendResults = await SendDataToPortsAsync(targetPorts, data);
            var successfulPorts = sendResults
                .Where(result => result.IsSuccess)
                .Select(result => result.PortName)
                .ToList();
            var failedResults = sendResults
                .Where(result => !result.IsSuccess)
                .ToList();

            if (ShowSentData && successfulPorts.Count > 0)
            {
                AddSentLogs(successfulPorts, displayContent);
            }

            RefreshPortStatistics(targetPorts);

            if (failedResults.Count > 0)
            {
                var failedPorts = string.Join(", ", failedResults.Select(result => result.PortName));
                StatusMessage = successfulPorts.Count == 0
                    ? $"发送失败: {failedPorts}"
                    : $"部分发送失败: 成功 {successfulPorts.Count}/{targetPorts.Count}，失败 {failedPorts}";
                return;
            }

            StatusMessage = targetPorts.Count == 1
                ? $"已发送 {data.Length} 字节"
                : $"已向 {targetPorts.Count} 个串口发送 {data.Length} 字节";
        }
        catch (Exception ex)
        {
            StatusMessage = $"发送失败: {ex.Message}";
        }
    }

    /// <summary>Nothing to send, or a send is already in flight.</summary>
    private bool CanSend() => !SendCommand.IsRunning && !string.IsNullOrEmpty(SendText);

    public async Task SendDataAsync(string portName, byte[] data)
    {
        await _serialPortService.SendDataAsync(portName, data);
    }

    public async Task SendTextAsync(string portName, string text)
    {
        await _serialPortService.SendTextAsync(portName, text, Encoding.UTF8);
    }

    /// <summary>
    /// Queues a locally generated (TX / tuning) entry for the next UI flush.
    /// </summary>
    /// <remarks>
    /// It goes through the same <see cref="_pendingLogBatches"/> queue as received data rather than
    /// writing into <see cref="AllLogs"/> / <see cref="DisplayLogs"/> directly. Doing it directly
    /// bypassed every safety net the receive path has: the FIFO trim (so a paused or
    /// high-frequency-send session grew the collections without bound), the search filter (so sent
    /// lines appeared in a filtered view they did not match), the per-flush batch window and the
    /// queued-count cap — and it fired an individual CollectionChanged per entry.
    /// </remarks>
    private void AddSentLog(string portName, LogEntry logEntry)
    {
        var queuedLogCount = Interlocked.Add(ref _queuedLogCount, 1);
        if (queuedLogCount > MaxQueuedLogEntries)
        {
            Interlocked.Add(ref _queuedLogCount, -1);
            Interlocked.Increment(ref _totalDropped);
            _logger.LogWarning("Dropping sent-log entry: queued logs exceeded limit. Port={Port}, Limit={Limit}",
                portName, MaxQueuedLogEntries);
            return;
        }

        _pendingLogBatches.Enqueue(new PendingLogBatch
        {
            PortName = portName,
            Logs = new List<LogEntry>(1) { logEntry }
        });

        SchedulePendingLogFlush();
    }

    private Task<PortSendResult[]> SendDataToPortsAsync(
        IReadOnlyList<string> targetPorts,
        byte[] data)
    {
        // Plain async sends: writes are async IO, so the old LongRunning dedicated threads
        // (one per port, blocking on GetResult) bought nothing but thread-per-port waste.
        return Task.WhenAll(targetPorts.Select(portName => SendDataToPortWorkerAsync(portName, data)));
    }

    private async Task<PortSendResult> SendDataToPortWorkerAsync(string portName, byte[] data)
    {
        try
        {
            await _serialPortService.SendDataAsync(portName, data);
            return new PortSendResult
            {
                PortName = portName,
                IsSuccess = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send data to {PortName}", portName);
            return new PortSendResult
            {
                PortName = portName,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private void AddSentLogs(IReadOnlyList<string> targetPorts, string displayContent)
    {
        foreach (var portName in targetPorts)
        {
            var logEntry = new LogEntry
            {
                PortName = portName,
                Content = displayContent,
                IsReceived = false,
                ColorHex = TxColorHexResolved
            };
            AddSentLog(portName, logEntry);
        }
    }

    public async Task SetTuningBinFilePathAsync(string filePath)
    {
        TuningBinFilePath = filePath ?? string.Empty;
        if (IsTuningWatching)
        {
            await RestartTuningWatchAsync();
        }
    }

    public async Task SetTuningDescriptorFilePathAsync(string filePath)
    {
        TuningDescriptorFilePath = filePath ?? string.Empty;
        await LoadTuningDescriptorCoreAsync();
        if (IsTuningWatching)
        {
            await RestartTuningWatchAsync();
        }
    }

    [RelayCommand]
    private async Task ReloadTuningDescriptorAsync()
    {
        await LoadTuningDescriptorCoreAsync();
        if (IsTuningWatching)
        {
            await RestartTuningWatchAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleTuningWatchAsync()
    {
        if (IsTuningWatching)
        {
            StopTuningWatch();
            TuningStatus = "已停止 tuning 监听";
            StatusMessage = TuningStatus;
            return;
        }

        await StartTuningWatchAsync(createBaseline: true);
    }

    [RelayCommand]
    private async Task SendCurrentTuningAsync()
    {
        await SendTuningFileAsync(force: true, cancellationToken: CancellationToken.None);
    }

    private async Task LoadTuningDescriptorCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(TuningDescriptorFilePath))
        {
            _tuningDescriptor = null;
            IsTuningDescriptorValid = false;
            TuningDescriptorStatus = "未选择 JSON 描述文件";
            TuningStatus = "未配置 tuning JSON";
            return;
        }

        try
        {
            _tuningDescriptor = await _tuningProtocolService.LoadDescriptorAsync(TuningDescriptorFilePath);
            IsTuningDescriptorValid = true;
            TuningDescriptorStatus = $"JSON 有效: {_tuningDescriptor.Name}";
            TuningStatus = "tuning JSON 已加载";
        }
        catch (Exception ex)
        {
            _tuningDescriptor = null;
            IsTuningDescriptorValid = false;
            TuningDescriptorStatus = $"JSON 无效: {ex.Message}";
            TuningStatus = TuningDescriptorStatus;
            if (IsTuningWatching)
            {
                StopTuningWatch();
            }
        }
    }

    private async Task StartTuningWatchAsync(bool createBaseline)
    {
        if (!CanUseTuning)
        {
            var message = BuildTuningUnavailableMessage();
            SetTuningUiState(() =>
            {
                TuningStatus = message;
                StatusMessage = message;
            });
            return;
        }

        if (_tuningDescriptor == null)
        {
            await LoadTuningDescriptorCoreAsync();
            if (_tuningDescriptor == null)
            {
                return;
            }
        }

        try
        {
            if (createBaseline)
            {
                if (!await WaitForStableTuningFileAsync(TuningBinFilePath, CancellationToken.None))
                {
                    throw new TuningProtocolException(
                        "tuning bin 文件仍在变化，未能建立稳定的基线；请稍后重试");
                }

                _lastTuningBaselineHash = await _tuningProtocolService.ComputeFileHashAsync(TuningBinFilePath);
                await _settingsService.SaveSettingAsync("TuningBaselineHash", _lastTuningBaselineHash);
            }

            ConfigureTuningWatcher();
            IsTuningWatching = true;
            await _settingsService.SaveSettingAsync("TuningIsWatching", 1);
            TuningStatus = createBaseline
                ? "已建立 tuning 基线，开始监听后续修改"
                : "已开始 tuning 监听";
            StatusMessage = TuningStatus;
        }
        catch (Exception ex)
        {
            StopTuningWatch();
            TuningStatus = $"启动 tuning 监听失败: {ex.Message}";
            _logger.LogError(ex, "Failed to start tuning watcher");
        }
    }

    private async Task RestartTuningWatchAsync()
    {
        StopTuningWatch(persistState: false);
        if (CanUseTuning)
        {
            await StartTuningWatchAsync(createBaseline: true);
            return;
        }

        await _settingsService.SaveSettingAsync("TuningIsWatching", 0);
        var message = BuildTuningUnavailableMessage();
        TuningStatus = message;
        StatusMessage = message;
    }

    private void StopTuningWatch(bool persistState = true)
    {
        _tuningChangeCts?.Cancel();
        _tuningChangeCts?.Dispose();
        _tuningChangeCts = null;

        if (_tuningFileWatcher != null)
        {
            _tuningFileWatcher.EnableRaisingEvents = false;
            _tuningFileWatcher.Changed -= OnTuningFileChanged;
            _tuningFileWatcher.Created -= OnTuningFileChanged;
            _tuningFileWatcher.Renamed -= OnTuningFileRenamed;
            _tuningFileWatcher.Dispose();
            _tuningFileWatcher = null;
        }

        if (persistState)
        {
            IsTuningWatching = false;
        }
        else
        {
            _suppressTuningWatchPersistence = true;
            try
            {
                IsTuningWatching = false;
            }
            finally
            {
                _suppressTuningWatchPersistence = false;
            }
        }
    }

    private void ConfigureTuningWatcher()
    {
        StopTuningWatch(persistState: false);

        var directory = Path.GetDirectoryName(TuningBinFilePath);
        var fileName = Path.GetFileName(TuningBinFilePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new InvalidOperationException("tuning bin 文件路径无效");
        }

        _tuningFileWatcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };
        _tuningFileWatcher.Changed += OnTuningFileChanged;
        _tuningFileWatcher.Created += OnTuningFileChanged;
        _tuningFileWatcher.Renamed += OnTuningFileRenamed;
        _tuningFileWatcher.EnableRaisingEvents = true;
    }

    private void OnTuningFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsCurrentTuningFile(e.FullPath))
        {
            ScheduleTuningAutoSend();
        }
    }

    private void OnTuningFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsCurrentTuningFile(e.FullPath))
        {
            ScheduleTuningAutoSend();
        }
    }

    private bool IsCurrentTuningFile(string path)
    {
        return !string.IsNullOrWhiteSpace(TuningBinFilePath) &&
               string.Equals(Path.GetFullPath(path), Path.GetFullPath(TuningBinFilePath), StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleTuningAutoSend()
    {
        var previous = Interlocked.Exchange(ref _tuningChangeCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var cts = _tuningChangeCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, cts.Token);
                await HandleTuningFileChangedAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // A newer file event superseded this one.
            }
            catch (ObjectDisposedException)
            {
                // CTS was disposed during shutdown, ignore.
            }
            catch (Exception ex)
            {
                SetTuningUiState(() => TuningStatus = $"tuning 自动发送失败: {ex.Message}");
                _logger.LogError(ex, "Tuning auto-send failed");
            }
        }, CancellationToken.None);
    }

    private async Task HandleTuningFileChangedAsync(CancellationToken cancellationToken)
    {
        if (!IsTuningWatching)
        {
            return;
        }

        if (!await WaitForStableTuningFileAsync(TuningBinFilePath, cancellationToken))
        {
            SetTuningUiState(() => TuningStatus = "tuning bin 仍在写入，已跳过本次发送");
            return;
        }

        if (!IsTuningWatching)
        {
            return;
        }

        var newHash = await _tuningProtocolService.ComputeFileHashAsync(TuningBinFilePath, cancellationToken);
        if (string.Equals(newHash, _lastTuningBaselineHash, StringComparison.OrdinalIgnoreCase))
        {
            SetTuningUiState(() => TuningStatus = "检测到 tuning 文件事件，但内容未变化");
            return;
        }

        await SendTuningFileAsync(force: false, cancellationToken);
    }

    private async Task SendTuningFileAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && !IsTuningWatching)
        {
            return;
        }

        if (!CanUseTuning)
        {
            var message = BuildTuningUnavailableMessage();
            SetTuningUiState(() =>
            {
                TuningStatus = message;
                StatusMessage = message;
            });
            return;
        }

        if (_tuningDescriptor == null)
        {
            await LoadTuningDescriptorCoreAsync();
            if (_tuningDescriptor == null)
            {
                return;
            }
        }

        await _tuningSendLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && !IsTuningWatching)
            {
                return;
            }

            SetTuningUiState(() =>
            {
                IsTuningBusy = true;
                TuningStatus = "正在打包 tuning bin...";
                StatusMessage = TuningStatus;
            });

            var targetPorts = _serialPortService.GetOpenPorts().ToList();
            if (targetPorts.Count == 0)
            {
                SetTuningUiState(() =>
                {
                    TuningStatus = "检测到 tuning 修改，但没有已打开串口，未更新基线";
                    StatusMessage = TuningStatus;
                });
                return;
            }

            var buildResult = await _tuningProtocolService.BuildAsync(TuningBinFilePath, _tuningDescriptor, cancellationToken);
            if (!force &&
                string.Equals(buildResult.BinSha256, _lastTuningBaselineHash, StringComparison.OrdinalIgnoreCase))
            {
                SetTuningUiState(() => TuningStatus = "tuning bin 内容未变化，无需发送");
                return;
            }

            SetTuningUiState(() =>
            {
                TuningStatus = $"正在发送 tuning: {buildResult.PacketCount} 包 -> {targetPorts.Count} 个串口";
                StatusMessage = TuningStatus;
            });

            var sendResults = await SendTuningToPortsAsync(targetPorts, buildResult, cancellationToken);
            var successfulPorts = sendResults
                .Where(result => result.IsSuccess)
                .Select(result => result.PortName)
                .ToList();
            var failedResults = sendResults
                .Where(result => !result.IsSuccess)
                .ToList();

            if (failedResults.Count > 0)
            {
                var failedPorts = string.Join(", ", failedResults.Select(result => result.PortName));
                SetTuningUiState(() =>
                {
                    if (successfulPorts.Count > 0)
                    {
                        AddTuningSentLogs(successfulPorts, buildResult);
                        RefreshPortStatistics(successfulPorts);
                    }

                    RefreshPortStatistics(failedResults.Select(result => result.PortName).ToList());
                    TuningStatus = $"tuning 部分发送失败: 成功 {successfulPorts.Count}/{targetPorts.Count}，失败 {failedPorts}";
                    StatusMessage = TuningStatus;
                });

                foreach (var failedResult in failedResults)
                {
                    _logger.LogWarning("Tuning send failed on {PortName}: {ErrorMessage}",
                        failedResult.PortName,
                        failedResult.ErrorMessage);
                }

                return;
            }

            _lastTuningBaselineHash = buildResult.BinSha256;
            await _settingsService.SaveSettingAsync("TuningBaselineHash", _lastTuningBaselineHash);

            SetTuningUiState(() =>
            {
                AddTuningSentLogs(targetPorts, buildResult);
                RefreshPortStatistics(targetPorts);
                TuningStatus = $"tuning 发送完成: {buildResult.TotalBytes} 字节，{buildResult.PacketCount} 包，{targetPorts.Count} 个串口";
                StatusMessage = TuningStatus;
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetTuningUiState(() =>
            {
                TuningStatus = $"tuning 发送失败: {ex.Message}";
                StatusMessage = TuningStatus;
            });
            _logger.LogError(ex, "Failed to send tuning data");
        }
        finally
        {
            SetTuningUiState(() => IsTuningBusy = false);
            _tuningSendLock.Release();
        }
    }

    private Task<PortSendResult[]> SendTuningToPortsAsync(
        IReadOnlyList<string> targetPorts,
        TuningBuildResult buildResult,
        CancellationToken cancellationToken)
    {
        var delayAfterSegment = BuildTuningDelayPlan(buildResult);
        return Task.WhenAll(targetPorts.Select(
            portName => SendTuningToPortWorkerAsync(portName, buildResult, delayAfterSegment, cancellationToken)));
    }

    private async Task<PortSendResult> SendTuningToPortWorkerAsync(
        string portName,
        TuningBuildResult buildResult,
        IReadOnlyList<bool> delayAfterSegment,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_serialPortService.IsPortOpen(portName))
            {
                _logger.LogWarning("Tuning send skipped: port {PortName} is not open", portName);
                return new PortSendResult
                {
                    PortName = portName,
                    IsSuccess = false,
                    ErrorMessage = $"Port {portName} is not open"
                };
            }

            for (var i = 0; i < buildResult.SendSegments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = buildResult.SendSegments[i];
                await _serialPortService.SendDataAsync(portName, segment.Data);

                if (delayAfterSegment[i])
                {
                    await Task.Delay(buildResult.DelayBetweenPacketsMs, cancellationToken);
                }
            }

            return new PortSendResult
            {
                PortName = portName,
                IsSuccess = true
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send tuning data to {PortName}", portName);
            return new PortSendResult
            {
                PortName = portName,
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static IReadOnlyList<bool> BuildTuningDelayPlan(TuningBuildResult buildResult)
    {
        var delayAfterSegment = new bool[buildResult.SendSegments.Count];
        var hasPacketAfter = false;
        for (var i = buildResult.SendSegments.Count - 1; i >= 0; i--)
        {
            var segment = buildResult.SendSegments[i];
            delayAfterSegment[i] = segment.IsPacket &&
                                   hasPacketAfter &&
                                   buildResult.DelayBetweenPacketsMs > 0;
            if (segment.IsPacket)
            {
                hasPacketAfter = true;
            }
        }

        return delayAfterSegment;
    }

    private void AddTuningSentLogs(IReadOnlyList<string> targetPorts, TuningBuildResult buildResult)
    {
        if (!ShowSentData)
        {
            return;
        }

        var summary = $"[TUNING] {Path.GetFileName(buildResult.BinFilePath)} | {buildResult.TotalBytes} bytes | {buildResult.PacketCount} packets";
        foreach (var portName in targetPorts)
        {
            var logEntry = new LogEntry
            {
                PortName = portName,
                Content = summary,
                IsReceived = false,
                ColorHex = TxColorHexResolved
            };
            AddSentLog(portName, logEntry);
        }
    }

    private void RefreshPortStatistics(IReadOnlyList<string> targetPorts)
    {
        foreach (var portName in targetPorts)
        {
            if (_portsByName.TryGetValue(portName, out var portVm))
            {
                var stats = _serialPortService.GetStatistics(portName);
                portVm.UpdateStatistics(stats);
            }
        }
    }

    /// <summary>
    /// Waits until the file's size and write time stop changing.
    /// </summary>
    /// <returns>
    /// <c>true</c> once two consecutive probes agree; <c>false</c> when the file is still changing after
    /// the full retry budget (~5 s).
    /// </returns>
    /// <remarks>
    /// The result matters: this used to fall through and return silently after the last attempt, so a
    /// file that was still being written (a build copying the .bin, a network share mid-sync) was hashed
    /// and sent as if it were final — the caller then stored a baseline hash for content that no longer
    /// existed on disk, which suppressed every subsequent auto-send as "content unchanged".
    /// </remarks>
    private async Task<bool> WaitForStableTuningFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            throw new FileNotFoundException("tuning bin 文件不存在", filePath);
        }

        long previousLength = -1;
        DateTime previousWriteTime = DateTime.MinValue;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(filePath);
            if (info.Exists &&
                info.Length == previousLength &&
                info.LastWriteTimeUtc == previousWriteTime)
            {
                return true;
            }

            previousLength = info.Length;
            previousWriteTime = info.LastWriteTimeUtc;
            await Task.Delay(250, cancellationToken);
        }

        _logger.LogWarning("tuning bin 文件在等待 5 秒后仍在变化，视为未稳定: {FilePath}", filePath);
        return false;
    }

    private void RefreshTuningAvailability()
    {
        // The master switch is first on purpose: with the feature disabled every send/watch entry point
        // must report "not enabled" rather than a misleading "please pick a .bin file", and no hidden
        // path may send anything even if something still called into it.
        CanUseTuning = IsTuningEnabled &&
                       IsTuningDescriptorValid &&
                       !string.IsNullOrWhiteSpace(TuningBinFilePath) &&
                       File.Exists(TuningBinFilePath);
    }

    private string BuildTuningUnavailableMessage()
    {
        if (!IsTuningEnabled)
        {
            return "Tuning 功能未启用";
        }

        if (string.IsNullOrWhiteSpace(TuningBinFilePath))
        {
            return "请选择 tuning bin 文件";
        }

        if (!File.Exists(TuningBinFilePath))
        {
            return "tuning bin 文件不存在";
        }

        if (string.IsNullOrWhiteSpace(TuningDescriptorFilePath))
        {
            return "请选择 tuning JSON 描述文件";
        }

        if (!IsTuningDescriptorValid)
        {
            return TuningDescriptorStatus;
        }

        return "tuning 当前不可用";
    }

    private void SetTuningUiState(Action update) => RunOnUiThread(update);

    /// <summary>Runs <paramref name="update"/> on the UI thread, immediately when already there.</summary>
    private void RunOnUiThread(Action update)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            update();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(() => update());
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            _logger.LogInformation("Clearing all logs");

            while (_pendingLogBatches.TryDequeue(out var pendingBatch))
            {
                Interlocked.Add(ref _queuedLogCount, -pendingBatch.Logs.Count);
            }

            // Drop the per-port text-assembly buffers too. They hold a persistent UTF-8 Decoder and the
            // unterminated tail line; leaving them behind means the next chunk on that port is glued to
            // text the user has just cleared, and the buffers stay allocated for the whole session.
            _lineAssemblers.Clear();

            // Clear both AllLogs and DisplayLogs collections
            AllLogs.Clear();
            DisplayLogs.Clear();
            
            // Reset match count
            MatchCount = 0;
            
            StatusMessage = "Logs cleared";
            _logger.LogInformation("All logs cleared successfully");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error clearing logs: {ex.Message}";
            _logger.LogError(ex, "Error clearing logs");
        }
    }

    [RelayCommand]
    private async Task ClosePortAsync(string portName)
    {
        try
        {
            StatusMessage = $"Closing port {portName}...";
            _logger.LogInformation("User requested to close port {PortName}", portName);

            // Stop a baud-rate scan for this port first. The scan closes the port for its duration
            // (up to ~40 s) and reopens it when it ends, so without cancelling it this close would
            // be undone a moment later — and the reopened handle would be invisible to both the
            // service and the UI, leaving the port impossible to close until the app exits.
            CancelBaudRateDetection(portName);

            // Drop the port's line-assembly buffer; a partial tail line would otherwise leak
            // into the next session on the same port name.
            _lineAssemblers.TryRemove(portName, out _);

            // Close the port first, then stop its file logger. Stopping the logger awaits its writer
            // (see FileLoggerService); the port is what the user asked to close, so bookkeeping must
            // not be able to sit in front of it — a slow log writer must never look like "the port
            // won't close". Closing first also captures the tail of the log instead of truncating it.
            await _serialPortService.ClosePortAsync(portName);

            await _fileLoggerService.StopLoggingAsync(portName);

            // Give OS time to fully release the serial port handle before allowing reopen
            await Task.Delay(500);

            if (_portsByName.TryGetValue(portName, out var portVm))
            {
                OpenPorts.Remove(portVm);
            }

            StatusMessage = $"Port {portName} closed successfully.";
            _logger.LogInformation("Port {PortName} closed successfully", portName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error closing port {portName}: {ex.Message}";
            _logger.LogError(ex, "Error closing port {PortName}", portName);
        }
    }

    // Per-port line assembly state. Serial chunks arrive at arbitrary byte offsets, so a
    // UTF-8 multi-byte character or a text line can be split across two DataReceived events.
    // Decoding each chunk in isolation turned split characters into U+FFFD (which the garbage
    // detector then flagged) and split lines into two broken entries. A persistent Decoder
    // carries incomplete byte sequences across chunks; a StringBuilder carries the partial
    // tail line until its newline arrives.
    private sealed class PortLineAssembler
    {
        public readonly StringBuilder Pending = new(128);

        // Scratch char buffer sized to the largest chunk seen (GetMaxCharCount upper bound).
        public char[] DecodeBuffer = Array.Empty<char>();

        // Decoder is not thread-safe, but all chunks for one port arrive on the serial
        // driver's single DataReceived worker thread, so access is serialized.
        public readonly Decoder Utf8Decoder = Encoding.UTF8.GetDecoder();
    }

    private readonly ConcurrentDictionary<string, PortLineAssembler> _lineAssemblers =
        new(StringComparer.OrdinalIgnoreCase);

    // If a stream never emits newlines, flush the pending tail as its own entry once it grows
    // past this size so the buffer stays bounded.
    private const int MaxPendingLineChars = 16 * 1024;

    // Splits the buffered characters into complete (terminator-delimited) lines, removes
    // them from `pending`, and leaves the unterminated tail in place for the next chunk.
    //
    // Two boundary invariants this scan relies on:
    //   * It only ever reads `pending[i]` and `pending[i + 1]` (guarded), never `pending[i - 1]`,
    //     so `i == 0` cannot underflow.
    //   * A trailing '\r' is left in the buffer instead of being consumed — it may be the first
    //     half of a "\r\n" whose '\n' arrives in the next chunk. Consuming it early would turn
    //     that '\n' into a bogus empty line on the next call.
    private static List<string> ExtractCompleteLines(StringBuilder pending)
    {
        var lines = new List<string>();
        var length = pending.Length;
        var lineStart = 0; // first char of the line currently being accumulated

        for (var i = 0; i < length; i++)
        {
            var c = pending[i];

            if (c == '\r')
            {
                // '\r' is the last buffered char: hold it (and the current line) so a '\n'
                // arriving next time completes this line rather than starting an empty one.
                if (i + 1 >= length)
                {
                    break;
                }

                lines.Add(pending.ToString(lineStart, i - lineStart));

                // "\r\n" consumes two chars; a lone '\r' consumes only itself.
                if (pending[i + 1] == '\n')
                {
                    i++;
                }

                lineStart = i + 1;
            }
            else if (c == '\n')
            {
                lines.Add(pending.ToString(lineStart, i - lineStart));
                lineStart = i + 1;
            }
        }

        // Everything before lineStart belonged to a terminated line; the remainder is the tail.
        if (lineStart > 0)
        {
            pending.Remove(0, lineStart);
        }

        if (pending.Length > MaxPendingLineChars)
        {
            // A stream that never emitted a terminator: flush the over-long tail as one line
            // (the 1000-char truncation downstream applies to it like any other line).
            lines.Add(pending.ToString());
            pending.Clear();
        }

        return lines;
    }

    private readonly ConcurrentQueue<PendingLogBatch> _pendingLogBatches = new();
    private int _isUiFlushScheduled = 0;
    private long _queuedLogCount = 0;
    private long _totalDataReceived = 0;
    private long _totalDropped = 0;
    
    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        var dataSize = e.Data?.Length ?? 0;
        Interlocked.Increment(ref _totalDataReceived);

        // Hoist the level check once so we don't box arguments for every LogTrace below at
        // production log levels. At Information level (App.xaml.cs:44) these all become a single
        // bool check.
        var traceEnabled = _logger.IsEnabled(LogLevel.Trace);

        if (traceEnabled)
        {
            _logger.LogTrace("OnDataReceived called: Port={Port}, Size={Size}bytes, QueuedLogs={QueuedLogs}",
                e.PortName, dataSize, Interlocked.Read(ref _queuedLogCount));
        }

        // Additional protection: Skip if data size is too large (potential garbage data)
        if (dataSize > 16384) // 16KB limit
        {
            Interlocked.Increment(ref _totalDropped);
            _logger.LogWarning("⚠️ Dropping oversized data packet: Size={Size}bytes, Port={Port}, Limit=16384",
                dataSize, e.PortName);
            return;
        }

        try
        {
            // Capture data on background thread
            if (e.Data == null || e.Data.Length == 0)
            {
                if (traceEnabled)
                {
                    _logger.LogTrace("Received null or empty data, skipping: Port={Port}", e.PortName);
                }
                return;
            }

            // Decode incrementally through the port's persistent decoder so UTF-8 multi-byte
            // sequences split across chunk boundaries don't become U+FFFD garbage. Encoding.UTF8
            // uses replacement fallback, so GetString can never throw — there is no decode
            // failure path to handle here.
            var portName = e.PortName;
            var assembler = _lineAssemblers.GetOrAdd(portName, _ => new PortLineAssembler());

            var maxChars = Encoding.UTF8.GetMaxCharCount(e.Data.Length);
            if (assembler.DecodeBuffer.Length < maxChars)
            {
                assembler.DecodeBuffer = new char[maxChars];
            }

            var charsDecoded = assembler.Utf8Decoder.GetChars(
                e.Data, 0, e.Data.Length, assembler.DecodeBuffer, 0, flush: false);
            assembler.Pending.Append(assembler.DecodeBuffer, 0, charsDecoded);

            var portColor = GetPortDisplayColor(portName);

            if (traceEnabled)
            {
                _logger.LogTrace("Decoded text: Length={Length} chars, Port={Port}", charsDecoded, portName);
            }

            // Only complete lines (terminated by \r\n, \r or \n) become log entries; the
            // partial tail stays in the buffer until its newline arrives in a later chunk.
            var lines = ExtractCompleteLines(assembler.Pending);

            if (traceEnabled)
            {
                _logger.LogTrace("Split into {LineCount} lines, Port={Port}", lines.Count, portName);
            }

            // Enhanced protection: Limit lines to prevent memory overflow and UI freezing
            var maxLines = Math.Min(lines.Count, 500); // Reduced from 1000 to 500 for better performance

            if (lines.Count > maxLines)
            {
                _logger.LogWarning("⚠️ Line count exceeds limit: Got {Count} lines, capping at {Max}, Port={Port}",
                    lines.Count, maxLines, portName);
            }
            
            // Pre-allocate to reduce reallocations
            var newLogs = new List<LogEntry>(maxLines > 1 ? maxLines : 1);
            
            var now = DateTime.Now;
            var validLineCount = 0;
            var garbageLineCount = 0;
            
            for (int i = 0; i < maxLines; i++)
            {
                var line = lines[i];
                
                // Skip only the last line if it's empty (common from splitting)
                if (i == maxLines - 1 && string.IsNullOrEmpty(line))
                    continue;
                
                // Enhanced garbage detection
                if (GarbageDataDetector.IsGarbageLine(line))
                {
                    garbageLineCount++;
                    
                    // Skip garbage lines to prevent UI pollution
                    if (garbageLineCount > 10) // If too many garbage lines, skip the rest
                    {
                        _logger.LogWarning("⚠️ Too many garbage lines detected, skipping remaining lines: Port={Port}, Skipped={Skipped}",
                            portName, maxLines - i - 1);
                        break;
                    }
                    
                    // Replace garbage line with indicator
                    line = "[Garbage data filtered]";
                }
                else
                {
                    validLineCount++;
                }
                
                // Limit line length to prevent UI issues
                if (line.Length > 1000)
                {
                    // Log the ORIGINAL length — the previous code logged after appending the
                    // truncation marker, recording 1000+marker instead of the real size.
                    var originalLength = line.Length;
                    line = line.Substring(0, 1000) + "...[truncated]";
                    _logger.LogDebug("Truncated long line: Port={Port}, OriginalLength={Original}", portName, originalLength);
                }

                var logEntry = new LogEntry
                {
                    PortName = portName,
                    Content = line,
                    Timestamp = now,
                    IsReceived = true,
                    ColorHex = portColor
                };

                newLogs.Add(logEntry);
            }

            // Hand the whole chunk to the file logger in one call. A per-entry fire-and-forget
            // Task here allocated an async state machine per line and swallowed exceptions;
            // FileLoggerService already batches internally, so the batch ends up queued anyway.
            if (newLogs.Count > 0)
            {
                _fileLoggerService.WriteLogs(portName, newLogs);
            }
            
            // Log statistics about data quality
            if (garbageLineCount > 0)
            {
                _logger.LogInformation("Data quality stats for {Port}: Valid={Valid}, Garbage={Garbage}, Total={Total}",
                    portName, validLineCount, garbageLineCount, newLogs.Count);
            }

            if (newLogs.Count == 0)
            {
                if (traceEnabled)
                {
                    _logger.LogTrace("No logs generated after processing, Port={Port}", portName);
                }
                return;
            }

            if (traceEnabled)
            {
                _logger.LogTrace("Generated {Count} log entries, Port={Port}", newLogs.Count, portName);
            }

            // If the user has paused the live view, the file log above has already captured this
            // data — do NOT feed it into the UI queue. New data only flows into the UI when
            // unpaused; the backlog during a pause is intentionally dropped from UI (still on disk).
            if (IsPaused)
            {
                if (traceEnabled)
                {
                    _logger.LogTrace("Paused — skipping UI enqueue for {Count} entries on {Port}",
                        newLogs.Count, portName);
                }
                return;
            }

            var queuedLogCount = Interlocked.Add(ref _queuedLogCount, newLogs.Count);
            if (queuedLogCount > MaxQueuedLogEntries)
            {
                Interlocked.Add(ref _queuedLogCount, -newLogs.Count);
                Interlocked.Increment(ref _totalDropped);
                _logger.LogWarning("⚠️ Dropping data update because queued logs exceed limit: Port={Port}, NewLogs={NewLogs}, QueuedLogs={QueuedLogs}, Limit={Limit}",
                    portName, newLogs.Count, queuedLogCount, MaxQueuedLogEntries);
                return;
            }

            _pendingLogBatches.Enqueue(new PendingLogBatch
            {
                PortName = portName,
                Logs = newLogs
            });

            if (traceEnabled)
            {
                _logger.LogTrace("Queued UI batch: Port={Port}, NewLogs={Count}, QueuedLogs={QueuedLogs}",
                    portName, newLogs.Count, queuedLogCount);
            }

            SchedulePendingLogFlush();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Critical error in OnDataReceived: Port={Port}, Size={Size}bytes",
                e.PortName, e.Data?.Length ?? 0);
        }
    }

    private void SchedulePendingLogFlush()
    {
        if (Interlocked.Exchange(ref _isUiFlushScheduled, 1) == 1)
        {
            return;
        }

        // Use a 50ms-debounced timer instead of immediately enqueueing the flush.
        // Why: under error storms or high-throughput data, the previous design kept
        // re-scheduling itself with zero gap, starving the UI thread of input events
        // (mouse wheel, selection updates).
        if (!_dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, StartFlushTimerOnUiThread))
        {
            Interlocked.Exchange(ref _isUiFlushScheduled, 0);
            _logger.LogError("❌ Failed to enqueue merged UI log flush");
        }
    }

    private void StartFlushTimerOnUiThread()
    {
        if (_flushTimer == null)
        {
            _flushTimer = _dispatcherQueue.CreateTimer();
            _flushTimer.Interval = TimeSpan.FromMilliseconds(FlushIntervalMs);
            _flushTimer.IsRepeating = false;
            _flushTimer.Tick += (_, _) => FlushPendingLogBatches();
        }

        if (!_flushTimer.IsRunning)
        {
            _flushTimer.Start();
        }
    }

    private void FlushPendingLogBatches()
    {
        var processedLogCount = 0;
        var batchesToFlush = new List<PendingLogBatch>();

        while (processedLogCount < MaxUiLogEntriesPerFlush &&
               _pendingLogBatches.TryDequeue(out var batch))
        {
            batchesToFlush.Add(batch);
            processedLogCount += batch.Logs.Count;
            Interlocked.Add(ref _queuedLogCount, -batch.Logs.Count);
        }

        try
        {
            if (batchesToFlush.Count == 0)
            {
                return;
            }

            var updateStartTime = DateTime.Now;
            var openPortMap = _portsByName;
            var searchTextSnapshot = SearchText;
            var isRegexValidSnapshot = IsRegexValid;
            var filterRegex = GetOrCreateSearchRegex(searchTextSnapshot, isRegexValidSnapshot);

            var estimatedLogCount = batchesToFlush.Sum(batch => batch.Logs.Count);
            var allLogsToAdd = new List<LogEntry>(estimatedLogCount);
            var displayLogsToAdd = new List<LogEntry>(estimatedLogCount);

            foreach (var pendingBatch in batchesToFlush)
            {
                if (!openPortMap.TryGetValue(pendingBatch.PortName, out var portVm))
                {
                    _logger.LogDebug("Ignoring queued data for closed port: {Port}", pendingBatch.PortName);
                    continue;
                }

                allLogsToAdd.AddRange(pendingBatch.Logs);
                _pendingStatsPorts.Add(pendingBatch.PortName);

                if (string.IsNullOrEmpty(searchTextSnapshot))
                {
                    displayLogsToAdd.AddRange(pendingBatch.Logs);
                }
                else if (filterRegex != null)
                {
                    // The port name is constant for the whole batch — match it once instead of
                    // running the regex over it for every entry.
                    var portNameMatches = MatchesSearch(pendingBatch.PortName, filterRegex);
                    foreach (var logEntry in pendingBatch.Logs)
                    {
                        if (portNameMatches || MatchesSearch(logEntry.Content, filterRegex))
                        {
                            displayLogsToAdd.Add(logEntry);
                        }
                    }
                }

                // Note: PortViewModel.Logs / FilteredLogs were previously updated per-item here,
                // but nothing in the UI binds to them — they were write-only. Each Add fired
                // CollectionChanged + PropertyChanged events, ran filter checks (locking), and
                // could trigger O(n) RemoveAt(0) when the per-port cap hit 10000. With 250 entries
                // per flush at ~20 Hz, that was a major UI-thread tax. Skip it.
            }

            if (allLogsToAdd.Count > 0)
            {
                AllLogs.AddRange(allLogsToAdd);
            }

            if (displayLogsToAdd.Count > 0)
            {
                DisplayLogs.AddRange(displayLogsToAdd);
            }

            TrimDisplayLogs();
            TrimLogCollection(AllLogs, AllLogsTrimThreshold, MaxDisplayLogs * 2, nameof(AllLogs));

            MatchCount = !string.IsNullOrEmpty(searchTextSnapshot) && isRegexValidSnapshot
                ? DisplayLogs.Count
                : 0;

            // Refresh port stats at most every StatsRefreshIntervalMs; the exception is the
            // final flush of a stream (queue drained), where we refresh so the counters
            // settle on their exact final values.
            var nowTick = Environment.TickCount64;
            if (_pendingStatsPorts.Count > 0 &&
                (nowTick - _lastStatsRefreshTick >= StatsRefreshIntervalMs || _pendingLogBatches.IsEmpty))
            {
                _lastStatsRefreshTick = nowTick;
                foreach (var portName in _pendingStatsPorts)
                {
                    if (openPortMap.TryGetValue(portName, out var portVm))
                    {
                        var stats = _serialPortService.GetStatistics(portName);
                        portVm.UpdateStatistics(stats);
                    }
                }
                _pendingStatsPorts.Clear();

                // Piggy-backs on the same ~4 Hz window as the per-port counters above.
                RefreshTrafficTotals();
            }

            var updateDuration = (DateTime.Now - updateStartTime).TotalMilliseconds;
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("✅ Merged UI flush completed: Batches={BatchCount}, Logs={LogCount}, Duration={Duration}ms, RemainingQueuedLogs={QueuedLogs}",
                    batchesToFlush.Count, allLogsToAdd.Count, updateDuration, Interlocked.Read(ref _queuedLogCount));
            }

            if (updateDuration > 100)
            {
                _logger.LogWarning("⚠️ Slow merged UI flush detected: Duration={Duration}ms, Batches={BatchCount}, Logs={LogCount}",
                    updateDuration, batchesToFlush.Count, allLogsToAdd.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Critical error while flushing merged UI logs");
        }
        finally
        {
            Interlocked.Exchange(ref _isUiFlushScheduled, 0);

            if (!_pendingLogBatches.IsEmpty)
            {
                SchedulePendingLogFlush();
            }
        }
    }

    /// <summary>
    /// Returns the compiled regex for <paramref name="searchText"/>, rebuilding it only when the
    /// pattern actually changes. Called from the 50ms flush loop and from FilterLogs — both on
    /// the UI thread, which is why recompiling per call (milliseconds with Compiled) was a
    /// direct hit on UI responsiveness while a search is active.
    /// </summary>
    private Regex? GetOrCreateSearchRegex(string searchText, bool isRegexValid)
    {
        if (string.IsNullOrEmpty(searchText) || !isRegexValid)
        {
            return null;
        }

        if (_cachedSearchRegex != null &&
            string.Equals(_cachedSearchRegexPattern, searchText, StringComparison.Ordinal))
        {
            return _cachedSearchRegex;
        }

        try
        {
            _cachedSearchRegex = new Regex(
                searchText,
                RegexOptions.IgnoreCase | RegexOptions.Compiled,
                TimeSpan.FromMilliseconds(100));
            _cachedSearchRegexPattern = searchText;
            return _cachedSearchRegex;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create search regex for live log filtering: Pattern={Pattern}", searchText);
            return null;
        }
    }

    private bool MatchesSearch(string text, Regex filterRegex)
    {
        try
        {
            return filterRegex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException ex)
        {
            _logger.LogWarning(ex, "Regex match timeout during live log filtering");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Regex match failed during live log filtering");
            return false;
        }
    }

    private void TrimLogCollection(
        List<LogEntry> collection,
        int trimThreshold,
        int targetCount,
        string collectionName)
    {
        if (collection.Count <= trimThreshold)
        {
            return;
        }

        var removeCount = collection.Count - targetCount;
        if (removeCount <= 0)
        {
            return;
        }

        collection.RemoveRange(0, removeCount);
        _logger.LogTrace("Trimmed {CollectionName}: Removed={Removed}, NewCount={Count}, Threshold={Threshold}, Target={Target}",
            collectionName, removeCount, collection.Count, trimThreshold, targetCount);
    }

    private void TrimDisplayLogs()
    {
        if (DisplayLogs.Count <= DisplayLogTrimThreshold)
        {
            return;
        }

        // Trim below the cap, not down to it: the overshoot is what keeps the next Reset hundreds of
        // entries away. Removing exactly the overflow meant a trim on every single flush once the cap
        // was reached (each one a full ListView rebuild at ~20 Hz).
        var target = MaxDisplayLogs - DisplayLogTrimHeadroom;
        var removeCount = DisplayLogs.Count - target;
        if (removeCount <= 0)
        {
            return;
        }

        DisplayLogs.RemoveFromStart(removeCount);

        _logger.LogTrace("Trimmed {CollectionName}: Removed={Removed}, NewCount={Count}, Threshold={Threshold}, Target={Target}",
            nameof(DisplayLogs), removeCount, DisplayLogs.Count, DisplayLogTrimThreshold, target);
    }

    private void OnPortStateChanged(object? sender, PortStateChangedEventArgs e)
    {
        // Capture values before dispatching to avoid closure issues
        var portName = e.PortName;
        var oldState = e.OldState;
        var newState = e.NewState;
        
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                StatusMessage = $"Port {portName}: {newState}";
                _logger.LogInformation("Port {PortName} state changed: {OldState} -> {NewState}",
                    portName, oldState, newState);
            }
            catch (Exception ex)
            {
                // Fallback logging if UI update fails
                _logger.LogError(ex, "Failed to update UI with state change");
            }
        });
    }

    private void OnErrorOccurred(object? sender, Services.ErrorEventArgs e)
    {
        // Capture values before dispatching to avoid closure issues
        var portName = e.PortName;
        var errorMessage = e.ErrorMessage;
        var exception = e.Exception;

        // Always log on background thread — logging never blocks UI.
        _logger.LogError(exception, "Error on port {PortName}", portName);

        // Throttle StatusMessage updates. Frame/Overrun error storms can fire 30+/sec
        // (wrong baud rate, line noise). Every update marshals to the UI thread and
        // re-renders the status bar, contributing to the wheel/selection lag the user
        // experiences. Keep the most recent error visible, drop the rest.
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastErrorStatusTicks);
        var elapsedMs = (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
        if (elapsedMs < ErrorStatusThrottleMs)
        {
            return;
        }
        Interlocked.Exchange(ref _lastErrorStatusTicks, nowTicks);

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                StatusMessage = $"Error on {portName}: {errorMessage}";
            }
            catch (Exception ex)
            {
                // Fallback logging if UI update fails
                _logger.LogError(ex, "Failed to update UI with error message");
            }
        });
    }

    private void OnBaudRateDetectionRequested(object? sender, Services.BaudRateDetectionRequestedEventArgs e)
    {
        // Capture values before dispatching to avoid closure issues
        var portName = e.PortName;
        var currentBaudRate = e.CurrentBaudRate;
        var reason = e.Reason;
        var cancellationToken = _shutdownCts.Token;

        // The dispatcher callback runs the state update and hands the actual work to a tracked Task.
        // Passing an `async () => …` lambda to TryEnqueue (what this used to do) makes it a de-facto
        // async void: its exceptions escape to the XAML unhandled-exception handler, and its
        // continuations keep running — closing and reopening serial ports — after the window and the
        // services have been torn down.
        RunOnUiThread(() => StartBaudRateDetection(portName, currentBaudRate, reason, cancellationToken));
    }

    // In-flight baud-rate scans, keyed by port name.
    //
    // A scan closes the port for its whole duration (up to ~40 s) and reopens it when it finishes,
    // entirely outside SerialPortService's bookkeeping. That made a user close during a scan a
    // no-op that was undone moments later — the port came back open while the UI had already
    // dropped it, so nothing could close it again short of ending the process. The handle kept
    // here is what lets a close (and shutdown) stop the scan instead of racing it.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _baudRateDetections =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the baud-rate scan running for <paramref name="portName"/>, if any.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan observes the cancellation at its next await and closes the port it opened on the way
    /// out. Used by the close paths; shutdown cancels them through <see cref="_shutdownCts"/>, which
    /// every scan is linked to.
    /// </para>
    /// <para>
    /// The dictionary entry is deliberately <em>not</em> removed here — the scan removes it in its
    /// own <c>finally</c>. Removing it now would let a new scan for the same port start while the
    /// cancelled one was still releasing its handle, putting two scans on one port again.
    /// </para>
    /// </remarks>
    private void CancelBaudRateDetection(string portName)
    {
        if (_baudRateDetections.TryGetValue(portName, out var detectionCts))
        {
            try
            {
                detectionCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The scan finished and disposed its own source between the lookup and here.
            }
        }
    }

    private void StartBaudRateDetection(
        string portName,
        int currentBaudRate,
        string reason,
        CancellationToken cancellationToken)
    {
        StatusMessage = $"检测到 {portName} 波特率可能不正确: {reason}";
        _logger.LogWarning("Baud rate detection requested for {PortName}: {Reason}", portName, reason);

        if (_baudRateDetectorService == null)
        {
            StatusMessage = $"波特率检测服务不可用，请手动调整 {portName} 的波特率";
            return;
        }

        StatusMessage = $"正在为 {portName} 检测最佳波特率...";
        _ = RunBaudRateDetectionAsync(portName, currentBaudRate, cancellationToken);
    }

    private async Task RunBaudRateDetectionAsync(
        string portName,
        int currentBaudRate,
        CancellationToken cancellationToken)
    {
        // One scan per port: the quality watchdog can request one repeatedly while the data stays
        // bad, and each scan holds the port closed for its whole run, so stacking them multiplies
        // both the port churn and the window in which a user close can be lost.
        using var detectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_baudRateDetections.TryAdd(portName, detectionCts))
        {
            _logger.LogInformation(
                "Baud rate detection for {PortName} is already running; ignoring the duplicate request",
                portName);
            return;
        }

        try
        {
            var token = detectionCts.Token;

            // The detector opens the port itself; as long as we still hold the port, every probe
            // fails with UnauthorizedAccessException and the whole detection burns ~40s only to
            // report "无法确定". Close it for the duration, then reopen with the detected (or
            // original) baud rate.
            await _serialPortService.ClosePortAsync(portName);
            await Task.Delay(500, token);

            var detectionResults = await _baudRateDetectorService!
                .DetectOptimalBaudRateAsync(portName, cancellationToken: token);

            if (detectionResults.Count > 0 && detectionResults[0].ConfidenceScore > 0.5)
            {
                var bestBaudRate = detectionResults[0].BaudRate;
                RunOnUiThread(() => StatusMessage =
                    $"建议将 {portName} 波特率设置为 {bestBaudRate} (置信度: {detectionResults[0].ConfidenceScore:F2})");

                // 触发波特率建议事件，让UI显示警告
                BaudRateSuggested?.Invoke(this, new BaudRateSuggestionEventArgs
                {
                    PortName = portName,
                    CurrentBaudRate = currentBaudRate,
                    SuggestedBaudRate = bestBaudRate,
                    Reason = $"检测到数据质量不佳，建议波特率: {bestBaudRate}",
                    Confidence = detectionResults[0].ConfidenceScore,
                    ShouldAutoSwitch = detectionResults[0].ConfidenceScore > 0.8
                });

                // 如果置信度很高，可以自动切换
                if (detectionResults[0].ConfidenceScore > 0.8)
                {
                    RunOnUiThread(() => StatusMessage =
                        $"自动将 {portName} 波特率从 {currentBaudRate} 切换到 {bestBaudRate}");
                    await SwitchPortBaudRateAsync(portName, bestBaudRate);
                }
                else
                {
                    // 保持端口可用：用原波特率重开，等用户决定是否手动切换。
                    await ReopenPortWithBaudRateAsync(portName, currentBaudRate);
                }
            }
            else
            {
                RunOnUiThread(() => StatusMessage = $"无法为 {portName} 确定最佳波特率，请手动检查");
                await ReopenPortWithBaudRateAsync(portName, currentBaudRate);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown, or the user closed the port while the scan was running: leave it closed.
            // Reopening here would race the service teardown — or resurrect a port the user just
            // closed, which is exactly the bug this cancellation exists to prevent.
            _logger.LogInformation(
                "Baud rate detection for {PortName} was cancelled; leaving the port closed", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during baud rate detection for {PortName}", portName);
            RunOnUiThread(() => StatusMessage = $"波特率检测失败: {ex.Message}");

            // 尽力恢复端口，避免检测失败后端口一直处于关闭状态。
            try
            {
                await ReopenPortWithBaudRateAsync(portName, currentBaudRate);
            }
            catch (Exception reopenEx)
            {
                _logger.LogError(reopenEx, "Failed to reopen {PortName} after detection failure", portName);
            }
        }
        finally
        {
            _baudRateDetections.TryRemove(portName, out _);
        }
    }

    public async Task SwitchPortBaudRateAsync(string portName, int newBaudRate)
    {
        try
        {
            // 关闭当前端口（波特率检测流程中端口可能已被提前关闭，此时为无操作）
            await _serialPortService.ClosePortAsync(portName);

            // 等待一段时间确保端口完全释放
            await Task.Delay(500);

            await ReopenPortWithBaudRateAsync(portName, newBaudRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching baud rate for {PortName}", portName);
            throw;
        }
    }

    private async Task ReopenPortWithBaudRateAsync(string portName, int baudRate)
    {
        // Never resurrect a port the user closed while the scan/switch was in flight. Such a reopen
        // produced an open handle that was in neither OpenPorts nor SerialPortService's map, so it
        // could not be closed from the UI at all — the user had to exit the app to free COMx.
        if (!_portsByName.ContainsKey(portName))
        {
            _logger.LogInformation(
                "Not reopening {PortName}: the port is no longer open", portName);
            return;
        }

        var config = new SerialPortConfig
        {
            PortName = portName,
            BaudRate = baudRate,
            DataBits = DataBits,
            StopBits = StopBits,
            Parity = Parity
        };

        var opened = await _serialPortService.OpenPortAsync(config);

        if (opened)
        {
            // 重置验证状态
            _dataValidationService?.ResetValidationState(portName);

            _logger.LogInformation("Successfully opened {PortName} with baud rate {BaudRate}", portName, baudRate);
        }
        else
        {
            _logger.LogError("Failed to reopen {PortName} with baud rate {BaudRate}", portName, baudRate);
        }
    }

    public string GetLogDirectory()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return System.IO.Path.Combine(documentsPath, "SerialPortTool", "Logs");
    }

    private bool _disposed;

    /// <summary>
    /// Cancelled when this ViewModel is disposed, and observed by the long-running background flows
    /// it starts (currently the baud-rate scan, which opens the port once per candidate rate and can
    /// otherwise keep running — and keep touching serial ports — after the window is gone).
    /// </summary>
    private readonly CancellationTokenSource _shutdownCts = new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel before tearing anything else down: a running scan closes and reopens the port, and
        // must not do that while the services behind it are being disposed.
        try { _shutdownCts.Cancel(); } catch (ObjectDisposedException) { }

        _filterDebounceTimer?.Dispose();
        _filterDebounceTimer = null;
        if (_flushTimer != null)
        {
            try { _flushTimer.Stop(); } catch { }
            _flushTimer = null;
        }
        StopTuningWatch(persistState: false);
        _tuningSendLock.Dispose();

        _serialPortService.DataReceived -= OnDataReceived;
        _serialPortService.PortStateChanged -= OnPortStateChanged;
        _serialPortService.ErrorOccurred -= OnErrorOccurred;
        _settingsService.SettingsLoadFailed -= OnSettingsLoadFailed;

        OpenPorts.CollectionChanged -= OnOpenPortsChanged;

        if (_serialPortService is SerialPortService serialPortServiceInstance)
        {
            serialPortServiceInstance.BaudRateDetectionRequested -= OnBaudRateDetectionRequested;
        }

        // Disposed last, after every producer of a new request has been unsubscribed: reading
        // _shutdownCts.Token on a disposed source throws ObjectDisposedException, which would surface as
        // an unhandled exception from an event that arrives in the gap.
        _shutdownCts.Dispose();
    }
    
    /// <summary>
    /// 波特率建议事件参数
    /// </summary>
    public class BaudRateSuggestionEventArgs : EventArgs
    {
        public string PortName { get; set; } = string.Empty;
        public int CurrentBaudRate { get; set; }
        public int SuggestedBaudRate { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public bool ShouldAutoSwitch { get; set; }
    }
}

/// <summary>
/// Swatch entry for the TX colour picker. Holds a palette slot plus the brush for the active
/// appearance, so the picker previews the colour that will actually be rendered.
/// </summary>
public partial class PortColorOption : ObservableObject
{
    public PortColorOption(PortColorSlot slot)
    {
        Slot = slot;
        _brush = new SolidColorBrush(PortColorPalette.ParseHex(slot.SlotHex));
    }

    public PortColorSlot Slot { get; }

    /// <summary>Slot hex — this is the value bound to <c>SelectedValue</c>.</summary>
    public string Hex => Slot.SlotHex;

    public string Name => Slot.Name;

    [ObservableProperty]
    private SolidColorBrush _brush;

    public void RefreshBrush(bool isDark)
        => Brush = new SolidColorBrush(PortColorPalette.ParseHex(Slot.Resolve(isDark)));
}

/// <summary>
/// 单个串口的视图模型
/// </summary>
public partial class PortViewModel : ObservableObject
{
    [ObservableProperty]
    private string _portName = string.Empty;

    /// <summary>
    /// Palette slot for this port — persisted verbatim, never a resolved hex.
    /// </summary>
    [ObservableProperty]
    private string _colorHex = PortColorPalette.DefaultRxHex;

    /// <summary>
    /// Hex to paint with under the active appearance. Kept separate from <see cref="ColorHex"/> so the
    /// stored value stays a slot while the sidebar swatch and the log channel bar follow the theme.
    /// </summary>
    [ObservableProperty]
    private string _displayColorHex = PortColorPalette.DefaultRxHex;

    public void RefreshDisplayColor(bool isDark)
        => DisplayColorHex = PortColorPalette.Resolve(ColorHex, isDark);

    [ObservableProperty]
    private string _statisticsDisplay = "0 bytes";

    // The serialPortService / dispatcherQueue parameters are kept on the constructor signature so
    // existing call sites don't need to change and so these dependencies remain available if per-port
    // behavior is reintroduced. They are intentionally not stored — see commit removing
    // PortViewModel.Logs / FilteredLogs. The ILogFilterService parameter was dropped outright: it was
    // already being discarded here, and nothing else in the app used that dependency chain.
    public PortViewModel(
        string portName,
        ISerialPortService serialPortService,
        DispatcherQueue? dispatcherQueue = null)
    {
        _portName = portName;
        _ = serialPortService;
        _ = dispatcherQueue;
    }

    public void UpdateStatistics(PortStatistics stats)
    {
        StatisticsDisplay = $"↓ {MainViewModel.FormatDataSize(stats.ReceivedBytes)} | ↑ {MainViewModel.FormatDataSize(stats.SentBytes)}";
    }
}

/// <summary>
/// 垃圾数据检测工具类
/// </summary>
/// <remarks>
/// Rewritten as a single-pass span scan with a 256-bit distinct-char bitmap on the stack.
/// Replaces the previous LINQ implementation (line.Count(...), line.Distinct().Count(), and a
/// 3rd pattern-detection loop) which allocated enumerator + HashSet per line on the background
/// thread under high throughput. Thresholds are intentionally kept identical to the previous
/// behavior — do not "improve" them here without a deliberate behavior-change discussion.
/// </remarks>
public static class GarbageDataDetector
{
    /// <summary>
    /// 检测是否为垃圾数据行
    /// </summary>
    /// <param name="line">要检查的文本行</param>
    /// <returns>是否为垃圾数据</returns>
    public static bool IsGarbageLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return false;

        var span = line.AsSpan();
        var length = span.Length;

        // 256-bit bitmap covering chars 0..255. Any char > 255 is folded onto bit 255 — that's
        // fine because the "distinct chars" threshold is only used for the "too few distinct
        // chars" check, and distinguishing between high-codepoint runs doesn't affect that.
        Span<ulong> distinctBits = stackalloc ulong[4];
        var distinctCount = 0;
        var unprintableCount = 0;       // c < 32 && not in {\r \n \t}
        var extendedAsciiCount = 0;     // 127 < c < 256
        var unreadableCount = 0;        // c < 32 || c > 126 — the "pattern" counter
        var checkPatternStretch = length > 20;

        for (var i = 0; i < length; i++)
        {
            char c = span[i];

            // Distinct bitmap. Cap at 255 to keep the bitmap fixed-size.
            int bitIndex = c > 255 ? 255 : c;
            ref ulong word = ref distinctBits[bitIndex >> 6];
            ulong mask = 1UL << (bitIndex & 63);
            if ((word & mask) == 0)
            {
                word |= mask;
                distinctCount++;
            }

            if (c < 32 && c != '\r' && c != '\n' && c != '\t')
            {
                unprintableCount++;
            }

            if (c > 127 && c < 256)
            {
                extendedAsciiCount++;
            }

            if (checkPatternStretch && (c < 32 || c > 126))
            {
                unreadableCount++;
                // Original code returned true as soon as patternCount exceeded length * 0.3
                // inside a loop that only iterated up to length - 3. Use the same threshold;
                // dropping the length-3 boundary is fine because the threshold itself is what
                // determined the decision.
                if (unreadableCount * 10 > length * 3)
                {
                    return true;
                }
            }
        }

        // 检查是否包含过多不可打印字符 (>50%)
        if (length > 0 && unprintableCount * 2 > length)
        {
            return true;
        }

        // 检查是否为大量重复字符 (only 1-2 distinct chars, requires length > 10)
        if (length > 10 && distinctCount <= 2)
        {
            return true;
        }

        // 检查是否包含过多的扩展ASCII字符 (>70%)
        if (length > 0 && extendedAsciiCount * 10 > length * 7)
        {
            return true;
        }

        return false;
    }
}
