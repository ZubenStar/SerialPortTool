using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
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
using System.Text.Json;
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

    // How hard the receive path is currently being pushed. Written from each port's read thread,
    // read here on the UI thread to decide whether the flush may skip its optional work — see
    // IOutputPressureService and the 借鉴 Backlog in AGENTS.md.
    private readonly IOutputPressureService _outputPressure;

    private readonly INotificationService _notifications;

    // Quick-send library persistence and payload expansion. The observable projection the flyout
    // binds to lives on this ViewModel — see the Quick-send library region.
    private readonly ISnippetService _snippetService;

    // Highlight rule storage plus compilation. The compiled snapshot is handed to LogListView, which
    // owns the brushes — see the Keyword highlighting region.
    private readonly IHighlightRuleService _highlightRuleService;

    // Export writing. The file picker stays in the window (it needs the window handle); this ViewModel
    // owns the call, the status message and the snapshot's scope label.
    private readonly ILogExportService _logExportService;

    /// <summary>
    /// Last appearance handed to <see cref="ApplyEffectiveTheme"/>, so a rule edit made without an
    /// appearance change can still resolve its colours. Defaults to light, which is what the shell
    /// starts on before its first theme pass.
    /// </summary>
    private bool _appliedIsDark;

    /// <summary>
    /// Transient notifications, bound by the shell's bottom-right host.
    /// </summary>
    /// <remarks>
    /// Surfaced through the ViewModel because <c>RootLayout.DataContext</c> already *is* the
    /// ViewModel — the window has no reason to introduce a second binding root just for this stack.
    /// </remarks>
    public ReadOnlyObservableCollection<NotificationItem> Notifications => _notifications.Items;

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

    /// <summary>
    /// 扫描到的串口。
    /// </summary>
    /// <remarks>
    /// <see cref="AvailablePortItem"/> rather than a bare <see cref="string"/> since v2.5.0: the row has to
    /// say three things (which port, what device is behind it, and what the user calls it), and only the
    /// first fits in a string. Every read site uses <c>.PortName</c>; see the note on
    /// <c>AvailablePortItem.ToString</c> for why the type cannot degrade to a type name if a picker ever
    /// forgets its template.
    /// </remarks>
    [ObservableProperty]
    private ObservableCollection<AvailablePortItem> _availablePorts = new();

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

        // The target set only ever names open ports. Dropped here rather than at send time so the flyout's
        // ticks and the button's label can never disagree with what is actually open.
        PruneSendTargets();

        // "全部关闭" is only enabled while at least one port is open.
        CloseAllPortsCommand.NotifyCanExecuteChanged();
    }

    // ---- 发送目标（v2.4.0） ---------------------------------------------------------------------

    /// <summary>
    /// 发送目标：本次会话内选中的端口名。**空集合表示「全部已打开的串口」**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Session-only, deliberately not persisted. It is a transient intent, and a saved set naming a port that
    /// is no longer open would only mislead. An empty set meaning "all" is what keeps the default behaviour
    /// byte-for-byte what it was before targeting existed.
    /// </para>
    /// <para>
    /// Touched on the UI thread only (the flyout, <see cref="PruneSendTargets"/>, and
    /// <see cref="SendPayloadAsync"/>), so it needs no synchronisation.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> _sendTargetPorts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否发给全部已打开串口。</summary>
    public bool IsSendingToAllPorts => _sendTargetPorts.Count == 0;

    /// <summary>
    /// 「目标」按钮上的文字。
    /// </summary>
    /// <remarks>
    /// Always visible on the send row rather than hidden inside the flyout: the failure this feature invites is
    /// "I thought I was broadcasting", so the current scope has to be readable without opening anything.
    /// </remarks>
    public string SendTargetDisplay => _sendTargetPorts.Count switch
    {
        0 => "全部",
        1 => _sendTargetPorts.First(),
        _ => $"{_sendTargetPorts.Count} 个串口",
    };

    /// <summary>某个端口当前是否在目标内（「全部」时每个端口都算在内）。</summary>
    public bool IsSendTargetSelected(string portName)
        => _sendTargetPorts.Count == 0 || _sendTargetPorts.Contains(portName);

    /// <summary>
    /// 勾选 / 取消一个目标端口。
    /// </summary>
    /// <remarks>
    /// Un-ticking the first port materialises the full set, so the gesture reads as "everything except this
    /// one" rather than "only this one" — the opposite reading would make the very first click do the most
    /// surprising thing available. Re-ticking the last one collapses back to 全部, so the common case keeps
    /// reading as 全部 instead of as a list that happens to contain every port.
    /// </remarks>
    public void SetSendTargetSelected(string portName, bool selected)
    {
        if (_sendTargetPorts.Count == 0)
        {
            foreach (var port in OpenPorts)
            {
                _sendTargetPorts.Add(port.PortName);
            }
        }

        var changed = selected
            ? _sendTargetPorts.Add(portName)
            : _sendTargetPorts.Remove(portName);

        if (_sendTargetPorts.Count == OpenPorts.Count)
        {
            _sendTargetPorts.Clear();
            changed = true;
        }

        if (changed)
        {
            NotifySendTargetChanged();
        }
    }

    /// <summary>恢复为「全部」。</summary>
    public void SelectAllSendTargets()
    {
        if (_sendTargetPorts.Count == 0)
        {
            return;
        }

        _sendTargetPorts.Clear();
        NotifySendTargetChanged();
    }

    /// <summary>
    /// 剔除已经关闭的端口。
    /// </summary>
    /// <remarks>
    /// A stale name left in the set would make the flyout's ticks disagree with reality, and would keep a
    /// "N 个串口" label naming ports that no longer exist.
    /// </remarks>
    private void PruneSendTargets()
    {
        if (_sendTargetPorts.Count == 0)
        {
            return;
        }

        if (_sendTargetPorts.RemoveWhere(portName => !_portsByName.ContainsKey(portName)) > 0)
        {
            NotifySendTargetChanged();
        }
    }

    private void NotifySendTargetChanged()
    {
        OnPropertyChanged(nameof(SendTargetDisplay));
        OnPropertyChanged(nameof(IsSendingToAllPorts));
    }

    /// <summary>
    /// 本次发送的目标端口。
    /// </summary>
    /// <remarks>
    /// The intersection with <see cref="OpenPorts"/> is computed here rather than trusting the stored set, so
    /// a set that outlived its ports can never address a port that is gone.
    /// </remarks>
    private List<string> ResolveSendTargets()
        => OpenPorts
            .Select(port => port.PortName)
            .Where(IsSendTargetSelected)
            .ToList();

    /// <summary>
    /// 发送结果里的目标说明：只在"没有发给全部已打开串口"时才点名。
    /// </summary>
    /// <remarks>
    /// The one mistake this feature invites is believing a partial send was a broadcast, so a partial send has
    /// to say so in the place the outcome is already reported.
    /// </remarks>
    private string DescribeSendTargets(IReadOnlyList<string> targetPorts)
        => IsSendingToAllPorts ? string.Empty : $"（目标：{string.Join(", ", targetPorts)}）";

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
    // that only obscured where filtering actually happens (it is the SearchText/GetOrCreateSearchMatcher
    // path in FlushPendingLogBatches and FilterLogs). The service itself is still registered in DI for
    // a future rule-based filter UI; wiring it up is an explicit requirement, not an accident.

    [ObservableProperty]
    private ObservableCollection<string> _recentSearchTexts = new();

    /// <summary>Whether the recent-search panel has anything to show.</summary>
    public bool HasRecentSearches => RecentSearchTexts.Count > 0;

    /// <summary>
    /// Raw text of the search box. Typing only updates this; nothing is filtered until the query is
    /// committed (Enter / 搜索 button), so a half-typed pattern never triggers the O(n) rebuild that
    /// RequestFilterLogs performs.
    /// </summary>
    [ObservableProperty]
    private string _searchDraft = string.Empty;

    partial void OnSearchDraftChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchDraft));
        OnPropertyChanged(nameof(CanClearSearch));
    }

    public bool HasSearchDraft => !string.IsNullOrEmpty(SearchDraft);

    private string _searchText = string.Empty;

    /// <summary>
    /// The committed query, and the ONLY input to filtering: <see cref="FilterLogs"/> rebuilds from
    /// it and <see cref="FlushPendingLogBatches"/> decides from it whether a newly arriving line
    /// belongs on screen. Keeping it separate from <see cref="SearchDraft"/> is what stops
    /// uncommitted keystrokes from filtering live data.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                // Validate regex pattern (text mode is always valid)
                ValidateSearchPattern();

                // Update clear button visibility
                OnPropertyChanged(nameof(HasSearchText));
                OnPropertyChanged(nameof(CanClearSearch));

                // Debounce filter updates to reduce UI thrashing
                RequestFilterLogs();
            }
        }
    }

    /// <summary>
    /// Gets whether there is search text (for clear button visibility)
    /// </summary>
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    /// <summary>Clearing is offered while the box has text OR a query is still applied.</summary>
    public bool CanClearSearch => HasSearchDraft || HasSearchText;

    /// <summary>Placeholder that follows the active search mode.</summary>
    public string SearchPlaceholder => IsRegexSearch ? "输入正则表达式…" : "输入关键字…";

    /// <summary>
    /// Commits the draft as the active query (Enter key / 搜索 button) and records it in history.
    /// </summary>
    /// <remarks>
    /// Assigning <see cref="SearchText"/> already validates and arms the debounced rebuild, so this
    /// deliberately does not call FilterLogs itself — doing that on top of the setter is exactly how
    /// one keystroke used to trigger several full rebuilds.
    /// </remarks>
    [RelayCommand]
    private void ExecuteSearch()
    {
        var query = SearchDraft?.Trim() ?? string.Empty;

        // Reflect the trimmed value back so the box shows exactly what is applied.
        if (!string.Equals(SearchDraft, query, StringComparison.Ordinal))
        {
            SearchDraft = query;
        }

        SearchText = query;

        if (!string.IsNullOrEmpty(query))
        {
            AddToRecentSearches(query);
        }
    }

    /// <summary>Clears the box and drops the applied query, restoring the full log list.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        SearchDraft = string.Empty;
        SearchText = string.Empty;
    }

    /// <summary>
    /// Re-runs a query picked from the history panel.
    /// </summary>
    /// <remarks>
    /// Deliberately does not re-record the query: the history list is being browsed at that moment,
    /// and reordering it under the pointer is both surprising and a needless settings write.
    /// </remarks>
    [RelayCommand]
    private void ApplyRecentSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return;
        }

        SearchDraft = search;
        SearchText = search;
    }

    /// <summary>Empties the history list; the confirmation dialog lives in the view.</summary>
    [RelayCommand]
    private void ClearSearchHistory() => ClearRecentSearches();
    
    /// <summary>
    /// Add a search text to recent history.
    /// </summary>
    /// <remarks>
    /// Called from the commit path only (<see cref="ExecuteSearch"/>). It used to also be driven from
    /// the view's LostFocus handler, which is why the window needed a pair of "did I already save
    /// this" fields to avoid recording the same query twice.
    /// </remarks>
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

    // Cache for the search matcher. FlushPendingLogBatches runs ~20x/sec while a search is active
    // and FilterLogs runs on every committed query; both must reuse the same instance instead of
    // paying the RegexOptions.Compiled codegen cost on the UI thread each time. Keyed by
    // (query, mode, case) so flipping a switch can never hand back a stale matcher. Only touched on
    // the UI thread (SearchText setter, FilterLogs, flush timer), so no synchronization is needed.
    private string? _cachedSearchMatcherKey;
    private SearchMatcher? _cachedSearchMatcher;

    /// <summary>Search mode: <c>false</c> = literal text, <c>true</c> = regular expression. Persisted.</summary>
    [ObservableProperty]
    private bool _isRegexSearch = false;

    partial void OnIsRegexSearchChanged(bool value)
    {
        PersistSearchOption(SearchUseRegexSettingKey, value ? 1 : 0);
        OnPropertyChanged(nameof(SearchPlaceholder));
        ValidateSearchPattern();
        RequestFilterLogs();
    }

    /// <summary>Whether the query is matched case-sensitively. Persisted.</summary>
    [ObservableProperty]
    private bool _isCaseSensitiveSearch = false;

    partial void OnIsCaseSensitiveSearchChanged(bool value)
    {
        PersistSearchOption(SearchCaseSensitiveSettingKey, value ? 1 : 0);
        ValidateSearchPattern();
        RequestFilterLogs();
    }

    private const string SearchUseRegexSettingKey = "SearchUseRegex";
    private const string SearchCaseSensitiveSettingKey = "SearchCaseSensitive";

    /// <summary>Set while the persisted options are read at startup, so the read is not written back.</summary>
    private bool _skipSearchOptionPersistence = false;

    private void PersistSearchOption(string key, int value)
    {
        if (_skipSearchOptionPersistence)
        {
            return;
        }

        _ = SaveSearchOptionAsync(key, value);
    }

    private async Task SaveSearchOptionAsync(string key, int value)
    {
        try
        {
            await _settingsService.SaveSettingAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save search option {Key}", key);
        }
    }

    /// <summary>
    /// Reads the persisted search mode / case-sensitivity flags. The write-back is suppressed so a
    /// startup read is not mistaken for a user toggle.
    /// </summary>
    private async Task LoadSearchOptionsAsync()
    {
        try
        {
            _skipSearchOptionPersistence = true;
            IsRegexSearch = await _settingsService.LoadSettingAsync(SearchUseRegexSettingKey, 0) == 1;
            IsCaseSensitiveSearch = await _settingsService.LoadSettingAsync(SearchCaseSensitiveSettingKey, 0) == 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load search options");
        }
        finally
        {
            _skipSearchOptionPersistence = false;
        }
    }

    [ObservableProperty]
    private bool _isRegexValid = true;

    [ObservableProperty]
    private string _regexErrorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchCountDisplay))]
    private int _matchCount = 0;

    /// <summary>Match count for the status bar; empty when nothing is being filtered.</summary>
    public string MatchCountDisplay => MatchCount > 0 ? $"匹配 {MatchCount} 条" : string.Empty;

    /// <summary>
    /// Parses the committed query when it is a regex. Text mode is always valid and therefore never
    /// produces an error message, whatever characters the query contains.
    /// </summary>
    private void ValidateSearchPattern()
    {
        if (string.IsNullOrEmpty(SearchText) || !IsRegexSearch)
        {
            IsRegexValid = true;
            RegexErrorMessage = string.Empty;
            return;
        }

        try
        {
            // Validation only parses the pattern; the compiled instance is built once (and cached)
            // by GetOrCreateSearchMatcher when the pattern is actually used for filtering. The
            // options do not affect whether a pattern parses, so a default instance is enough.
            _ = new Regex(SearchText, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            IsRegexValid = true;
            RegexErrorMessage = string.Empty;
        }
        catch (ArgumentException ex)
        {
            IsRegexValid = false;
            RegexErrorMessage = $"正则表达式无效：{ex.Message}";
            _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", SearchText);
        }
    }

    // IsPaused replaces the old AutoScroll concept. UX:
    //   IsPaused = false (default): new data flows in and the view tracks the latest line — but only
    //     while the view is sitting at the bottom (IsLogPinnedToBottom). Scrolling away detaches the
    //     follow instead of being dragged back, so history can be read during a live stream; the
    //     回到最新 pill (or scrolling back down) re-attaches it.
    //     (Before v2.2.3 the follow was unconditional and every batch yanked the view back, so the
    //     only way to read history was to pause reception.)
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

    /// <summary>
    /// Whether the log view is sitting at the newest row — TwoWay from <c>LogListView</c>, which
    /// decides it from the scroll position.
    /// </summary>
    /// <remarks>
    /// While it is false the user is reading history. New lines keep arriving and keep being
    /// appended, but the display buffer is <b>not</b> pruned from the head: a trim publishes a
    /// <c>Reset</c>, which discards every realized container and slides the remaining rows up — i.e.
    /// it moves the very text the user is reading (see <c>TrimDisplayLogs</c>). Growth is bounded by
    /// <c>DisplayLogHardCap</c> instead.
    ///
    /// Only the UI thread writes this (the scroll position lives in the view), and every
    /// whole-list replacement (clear, a new search) sets it back to true, because after one of
    /// those there is no longer any history to be reading.
    /// </remarks>
    [ObservableProperty]
    private bool _isLogPinnedToBottom = true;

    partial void OnIsLogPinnedToBottomChanged(bool value)
    {
        // Re-attached: the trims that were skipped while the user was reading history are owed now.
        if (value)
        {
            TrimDisplayLogs();
        }
    }

    [ObservableProperty]
    private bool _sendAsHex = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _sendText = string.Empty;

    [ObservableProperty]
    private bool _showSentData = true;

    /// <summary>
    /// 发送文本时追加的行尾符。
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="SendLineEnding.None"/>: a terminator has to be asked for, because appending
    /// one silently would change the bytes of every payload on every existing install. The send box is a
    /// single-line <c>TextBox</c>, so without this a device that expects <c>\r</c> could only be driven
    /// through a snippet carrying an escape.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSplitMultilineSend))]
    private SendLineEnding _sendTerminator = SendLineEnding.None;

    /// <summary>「行尾符」选择器的选项。</summary>
    public ObservableCollection<SerialParameterOption<SendLineEnding>> SendTerminatorOptions { get; } = new()
    {
        new(SendLineEnding.None, "无"),
        new(SendLineEnding.Cr, "CR (\\r)"),
        new(SendLineEnding.Lf, "LF (\\n)"),
        new(SendLineEnding.CrLf, "CRLF (\\r\\n)"),
    };

    /// <summary>
    /// 多行内容逐行发送，每行各带一个行尾符。
    /// </summary>
    /// <remarks>
    /// On by default, so picking a terminator immediately behaves the way a device expects from a
    /// multi-line paste. Turning it off sends the block as one piece with a single terminator at the end.
    /// </remarks>
    [ObservableProperty]
    private bool _splitMultilineSend = true;

    /// <summary>「多行拆分」只有在选了行尾符之后才有意义，所以未选时把开关置灰。</summary>
    public bool CanSplitMultilineSend => SendTerminator != SendLineEnding.None;

    /// <summary>
    /// 最近发送过的内容，最新在前。
    /// </summary>
    /// <remarks>
    /// A plain list rather than an ObservableCollection: nothing binds to it (the recall is the keyboard),
    /// so per-change notifications would be work nobody observes. The single instance is kept and its
    /// contents swapped, so the public view never has to be re-read.
    /// </remarks>
    private readonly List<string> _recentSendTexts = new();

    /// <summary>发送历史（最新在前），发送框 ↑/↓ 从这里召回。</summary>
    public IReadOnlyList<string> RecentSendTexts => _recentSendTexts;

    /// <summary>召回游标在 <see cref="RecentSendTexts"/> 中的位置；-1 表示当前没有在召回。</summary>
    private int _sendHistoryIndex = -1;

    /// <summary>开始召回之前发送框里的内容；↓ 走过最新一条时把它还回去。</summary>
    private string? _sendHistoryDraft;

    /// <summary>
    /// True while a recall assignment is in flight, so <see cref="OnSendTextChanged"/> does not treat the
    /// walk's own writes as a user edit — that would end the walk on its very first step.
    /// </summary>
    private bool _isRecallingSendHistory;

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

    /// <summary>日志行字号的设置键。</summary>
    private const string LogFontSizeSettingKey = "LogFontSize";

    /// <summary>
    /// 日志行字号，绑定到 <c>LogListView.FontSize</c>，行内文本继承它。
    /// </summary>
    /// <remarks>
    /// A double bound straight to the control's inherited <c>FontSize</c>. The row template no longer hard-codes
    /// a size, which is what makes the setting expressible at all — and what lets the control's wheel step and
    /// bottom slack follow it. The bounds and the step live in <see cref="LogFontScale"/>, shared with the
    /// loader that reads a hand-edited settings file.
    /// </remarks>
    [ObservableProperty]
    private double _logFontSize = LogFontScale.Default;

    partial void OnLogFontSizeChanged(double value)
    {
        _ = _settingsService.SaveSettingAsync(LogFontSizeSettingKey, (int)Math.Round(value));
    }

    /// <summary>调整日志字号（Ctrl+滚轮 / Ctrl+加号、减号）。</summary>
    public void AdjustLogFontSize(int steps)
        => LogFontSize = LogFontScale.Adjust(LogFontSize, steps);

    /// <summary>恢复默认日志字号（Ctrl+0）。</summary>
    public void ResetLogFontSize() => LogFontSize = LogFontScale.Default;

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
            // Through LogRowColorPalette, not PortColorPalette directly: an event row's grey is not a palette
            // slot, and Resolve returns an unrecognised hex unchanged — so the direct call would leave every
            // event row on the previous appearance while the data rows moved.
            entry.ColorHex = LogRowColorPalette.Resolve(entry.Kind, entry.ColorHex, isDark);
        }

        // Both swatch sets, not just the TX one: the sidebar's colour menu is built from
        // PortColorOptions on open and must preview the colour that will actually be rendered.
        foreach (var option in TxColorOptions)
        {
            option.RefreshBrush(isDark);
        }

        foreach (var option in PortColorOptions)
        {
            option.RefreshBrush(isDark);
        }

        // Highlight rules resolve their colours from the same palette, so an appearance switch has to
        // recompile them as well. Recompiling bumps the snapshot generation, which is what invalidates
        // every per-entry match cache at once — no walk over the log buffer — and the property change
        // is what makes the realized rows re-decorate.
        _appliedIsDark = isDark;
        RebuildHighlightRules();
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
    public ObservableCollection<PortColorOption> TxColorOptions { get; } = BuildOptions(PortColorPalette.TxOptions);

    /// <summary>
    /// Swatch options for the port-colour menu, one per palette slot; brushes follow the active
    /// appearance.
    /// </summary>
    /// <remarks>
    /// The single source the sidebar's colour menu builds its items from
    /// (<c>MainWindow.PortColorFlyout_Opening</c>). Before v2.2.4 the same ten swatches were also
    /// declared in <c>MainWindow.xaml</c> with ten matching <c>AppPortColorNBrush</c> resources in
    /// <c>Themes/Tokens.xaml</c>; both are gone, so the palette now lives in exactly one place.
    /// </remarks>
    public ObservableCollection<PortColorOption> PortColorOptions { get; } = BuildOptions(PortColorPalette.Slots);

    private static ObservableCollection<PortColorOption> BuildOptions(IReadOnlyList<PortColorSlot> slots)
    {
        var options = new ObservableCollection<PortColorOption>();
        foreach (var slot in slots)
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

    /// <summary>Settings key for <see cref="IsAdvancedParametersExpanded"/>.</summary>
    private const string AdvancedParametersExpandedSettingKey = "AdvancedParametersExpanded";

    /// <summary>
    /// Whether the sidebar's 「高级参数」 section — data bits / stop bits / parity / flow control /
    /// encoding, plus the automation card — is expanded.
    /// </summary>
    /// <remarks>
    /// Collapsed by default: those pickers are set once for a device and then left alone, while they used
    /// to take up most of the rail's height. The header keeps carrying the current values through
    /// <see cref="LineParametersSummary"/>, so a folded section cannot be mistaken for an unset one.
    /// </remarks>
    [ObservableProperty]
    private bool _isAdvancedParametersExpanded;

    partial void OnIsAdvancedParametersExpandedChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync(AdvancedParametersExpandedSettingKey, value ? 1 : 0);
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

    /// <summary>
    /// Number of ports the last scan found, republished for the rail's 可用串口 card header.
    /// </summary>
    /// <remarks>
    /// A plain int rather than binding straight to <c>AvailablePorts.Count</c>: the header has to be
    /// able to say "0" honestly (a scan that found nothing is a different answer from a scan that has
    /// not run yet, and both look identical in an empty list). It is written in exactly one place —
    /// the end of <c>ScanPortsAsync</c>, which is the only code that mutates the collection.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailablePortCountDisplay))]
    private int _availablePortCount;

    /// <summary>Scan-result count for the 可用串口 card header.</summary>
    public string AvailablePortCountDisplay => AvailablePortCount == 0 ? "未扫描到" : $"{AvailablePortCount} 个";

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

        // A real edit ends a recall walk; the writes the walk itself makes must not, or ↑ would reset the
        // walk the moment it moved.
        if (!_isRecallingSendHistory)
        {
            ResetSendHistoryNavigation();
        }
    }

    partial void OnSendTerminatorChanged(SendLineEnding value)
    {
        _ = _settingsService.SaveSettingAsync("SendTerminator", (int)value);
    }

    partial void OnSplitMultilineSendChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync("SplitMultilineSend", value ? 1 : 0);
    }

    partial void OnShowSentDataChanged(bool value)
    {
        _ = _settingsService.SaveSettingAsync("ShowSentData", value ? 1 : 0);
    }

    // The line parameters are only persisted when a port is opened (see SaveLineParametersAsync); the
    // handlers exist so a change made while ports are live is not silently ignored.
    partial void OnDataBitsChanged(int value) => HintThatLineParametersApplyOnNextOpen();

    partial void OnStopBitsChanged(System.IO.Ports.StopBits value) => HintThatLineParametersApplyOnNextOpen();

    partial void OnParityChanged(System.IO.Ports.Parity value) => HintThatLineParametersApplyOnNextOpen();

    partial void OnHandshakeChanged(System.IO.Ports.Handshake value) => HintThatLineParametersApplyOnNextOpen();

    partial void OnTextEncodingNameChanged(string value)
    {
        if (_isLoadingSerialParameters)
        {
            // Startup read: nothing was changed by the user, so there is nothing to persist or apply.
            return;
        }

        _ = ApplyTextEncodingChangeAsync(value);
    }

    /// <summary>
    /// Tells the user that a changed line parameter does not reach ports that are already open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SerialPort</c> applies data bits / stop bits / parity / handshake when the handle is opened, so
    /// the only way to apply them to a live port is a close-and-reopen — which is exactly the
    /// close/reconnect race the "do not regress" list forbids doing implicitly. A status-bar line is
    /// therefore the whole behaviour: it states the truth without touching a live port.
    /// </para>
    /// <para>
    /// Deliberately not a notification-stack entry: that channel is reserved for what the user must
    /// notice, and moving a combo box is not one of them.
    /// </para>
    /// </remarks>
    private void HintThatLineParametersApplyOnNextOpen()
    {
        if (_isLoadingSerialParameters || OpenPorts.Count == 0)
        {
            return;
        }

        StatusMessage = "串口参数已改为对「下次打开」生效：已打开的串口需关闭后重新打开";
    }

    /// <summary>
    /// Reads a line parameter from a plain int setting, rejecting anything outside
    /// <paramref name="options"/>.
    /// </summary>
    /// <remarks>
    /// Keeps a hand-edited <c>settings.json</c> from turning into a port that can never be opened. The
    /// fallback is always the shipped default rather than "clamp to something similar".
    /// </remarks>
    private static T PickAllowedValue<T>(int raw, ObservableCollection<SerialParameterOption<T>> options, T fallback)
        where T : struct
    {
        var candidate = typeof(T).IsEnum
            ? (T)Enum.ToObject(typeof(T), raw)
            : (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);

        foreach (var option in options)
        {
            if (EqualityComparer<T>.Default.Equals(option.Value, candidate))
            {
                return option.Value;
            }
        }

        return fallback;
    }

    /// <summary>
    /// Persists the line parameters a later open will use.
    /// </summary>
    /// <remarks>
    /// Called only from the two open paths, matching how the baud-rate settings are already saved: these
    /// are "what the next open uses", not live state.
    /// </remarks>
    private async Task SaveLineParametersAsync()
    {
        await _settingsService.SaveSettingAsync("DataBits", DataBits);
        await _settingsService.SaveSettingAsync("StopBits", (int)StopBits);
        await _settingsService.SaveSettingAsync("Parity", (int)Parity);
        await _settingsService.SaveSettingAsync("Handshake", (int)Handshake);
    }

    /// <summary>
    /// Applies a text-encoding change: persists it, and pushes it to every open port immediately.
    /// </summary>
    /// <remarks>
    /// Unlike the line parameters, the encoding takes effect without a close/reopen — it is a decode
    /// concern, and the mechanism for changing it is dropping the port's line assembler so the next chunk
    /// builds one for the new encoding. That is the same operation <see cref="ClearLogs"/> and
    /// <see cref="ClosePortAsync"/> already perform, so it adds no new way for the read thread and the UI
    /// thread to meet. A partially received line is dropped with the assembler on purpose: it was decoded
    /// with the encoding the user just rejected.
    /// </remarks>
    private async Task ApplyTextEncodingChangeAsync(string requestedName)
    {
        var normalized = SerialEncodings.Normalize(requestedName);
        if (!SerialEncodings.IsSupported(requestedName))
        {
            _logger.LogWarning("Unknown text encoding '{Encoding}'; using {Fallback}", requestedName, normalized);
        }

        // Snapshot the ports before the first await. OpenPorts is a UI-bound collection and StatusMessage is
        // a bound property, so neither may be touched from a continuation that could land on a pool thread;
        // everything else below (the service map, the assembler dictionary) is deliberately thread-safe.
        var openPorts = OpenPorts.ToList();

        await _settingsService.SaveSettingAsync(SerialEncodingSettingKey, normalized);

        foreach (var port in openPorts)
        {
            _serialPortService.SetPortTextEncoding(port.PortName, normalized);
            await _settingsService.SaveSettingAsync(PortEncodingSettingKey(port.PortName), normalized);
            _lineAssemblers.TryRemove(port.PortName, out _);
        }

        StatusMessage = openPorts.Count == 0
            ? $"文本编码已设为 {normalized}"
            : $"文本编码已设为 {normalized}，已对 {openPorts.Count} 个串口即时生效";
    }

    /// <summary>
    /// The encoding a port opens with: its remembered per-port value, else the current default.
    /// </summary>
    /// <remarks>
    /// Per port rather than global, following <c>PortColor_&lt;port&gt;</c>: two devices on two ports
    /// genuinely can use two character sets, and the value has to survive a close/reopen in one session.
    /// </remarks>
    private async Task<string> ResolvePortEncodingForOpenAsync(string portName)
    {
        var remembered = await _settingsService.LoadSettingAsync(
            PortEncodingSettingKey(portName), TextEncodingName);

        if (!SerialEncodings.IsSupported(remembered))
        {
            _logger.LogWarning("Unknown saved text encoding '{Encoding}' for port {PortName}; using {Fallback}",
                remembered, portName, SerialEncodings.Normalize(remembered));
        }

        return SerialEncodings.Normalize(remembered);
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

    // While the user is scrolled away from the newest row, trims are suspended (they would slide the
    // content out from under the cursor), so the buffer is allowed to grow past its normal ceiling
    // up to here. Well above the working set, still bounded: a 20 Hz stream needs minutes to reach
    // it, and a runaway one is stopped instead of growing without limit.
    private const int DisplayLogHardCap = MaxDisplayLogs * 3;

    private const int AllLogsTrimThreshold = MaxDisplayLogs * 2 + 400;
    private const int MaxQueuedLogEntries = MaxDisplayLogs * 4;
    // Smaller batches at a fixed cadence give shorter, more uniform UI blocks. Bigger batches feel
    // like noticeable hitches. With 100 items at 50ms cadence we can sustain ~2000 lines/sec while
    // keeping each UI flush short enough that the user's wheel/drag input stays responsive.
    private const int MaxUiLogEntriesPerFlush = 100;
    private const int FlushIntervalMs = 50;

    // Adaptive cadence (v2.2.4). While IOutputPressureService reports a flood the flush runs less
    // often and applies a proportionally larger batch.
    //
    // The two knobs must move together, and that is the whole point: the flush's dominant cost is the
    // Reset it publishes (AddRange above 32 entries collapses into one), which discards every realized
    // container and rebuilds the visible window — a cost bounded by the viewport, not by the batch
    // size. Fewer flushes therefore means proportionally fewer rebuilds for the same total entries.
    // Widening the interval *without* widening the budget would instead cut the drain rate from 2000
    // to ~670 entries/s, and MaxQueuedLogEntries would then start dropping lines during exactly the
    // flood the degradation exists to survive.
    private const int DegradedFlushIntervalMs = 150;
    private const int MaxUiLogEntriesPerFlushDegraded =
        MaxUiLogEntriesPerFlush * (DegradedFlushIntervalMs / FlushIntervalMs);

    private const int ErrorStatusThrottleMs = 250;

    /// <summary>
    /// 同一事件行在窗口内重复时折叠成计数（见 <see cref="ReportPortEvent"/>）。
    /// </summary>
    /// <remarks>
    /// One second: the same order of magnitude as the status bar's own throttle. A frame-error storm runs at
    /// 30+/s, so without a window the log would be mostly error rows; with a much longer one the "how bad is
    /// it" signal would arrive too late to correlate with what the device was doing.
    /// </remarks>
    private const int EventRepeatWindowMs = 1000;

    /// <summary>事件行的重复计数窗口。只在 UI 线程上访问，因此不需要锁。</summary>
    private readonly EventAggregateWindow _eventAggregates = new();

    private DispatcherQueueTimer? _eventAggregateTimer;
    // Port stat counters don't need 20Hz UI updates; ~4Hz is visually identical and skips
    // most per-flush GetStatistics/UpdateStatistics work.
    private const int StatsRefreshIntervalMs = 250;

    private DispatcherQueueTimer? _flushTimer;

    // The interval currently assigned to _flushTimer, so an unchanged cadence does not reassign it
    // (Interval can only be assigned while the timer is stopped, and the getter is not a reliable
    // comparison source). UI thread only.
    private int _appliedFlushIntervalMs;

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

    // The five line parameters below all feed LineParametersSummary, which is what the sidebar's folded
    // 「高级参数」 header shows — so each one has to republish it. The toolkit attribute rather than a
    // hand-written partial: the existing OnXxxChanged handlers belong to HintThatLineParametersApplyOnNextOpen.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineParametersSummary))]
    private int _dataBits = 8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineParametersSummary))]
    private System.IO.Ports.StopBits _stopBits = System.IO.Ports.StopBits.One;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineParametersSummary))]
    private System.IO.Ports.Parity _parity = System.IO.Ports.Parity.None;

    /// <summary>
    /// Flow control (handshake) used when a port is opened.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="System.IO.Ports.Handshake.None"/>, which is also what <c>SerialPort</c>
    /// defaults to — an unchanged install therefore sends exactly the bytes it always did.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineParametersSummary))]
    private System.IO.Ports.Handshake _handshake = System.IO.Ports.Handshake.None;

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

    /// <summary>Stop-bit choices. Labels are written out because <c>OnePointFive</c> is not a name.</summary>
    public ObservableCollection<SerialParameterOption<System.IO.Ports.StopBits>> StopBitsOptions { get; } = new()
    {
        new(System.IO.Ports.StopBits.One, "1"),
        new(System.IO.Ports.StopBits.OnePointFive, "1.5"),
        new(System.IO.Ports.StopBits.Two, "2"),
    };

    /// <summary>Parity choices.</summary>
    public ObservableCollection<SerialParameterOption<System.IO.Ports.Parity>> ParityOptions { get; } = new()
    {
        new(System.IO.Ports.Parity.None, "无校验"),
        new(System.IO.Ports.Parity.Odd, "奇校验"),
        new(System.IO.Ports.Parity.Even, "偶校验"),
        new(System.IO.Ports.Parity.Mark, "标记校验"),
        new(System.IO.Ports.Parity.Space, "空格校验"),
    };

    /// <summary>Flow-control choices.</summary>
    public ObservableCollection<SerialParameterOption<System.IO.Ports.Handshake>> HandshakeOptions { get; } = new()
    {
        new(System.IO.Ports.Handshake.None, "无"),
        new(System.IO.Ports.Handshake.XOnXOff, "软件 XON/XOFF"),
        new(System.IO.Ports.Handshake.RequestToSend, "硬件 RTS/CTS"),
        new(System.IO.Ports.Handshake.RequestToSendXOnXOff, "RTS/CTS + XON/XOFF"),
    };

    /// <summary>
    /// True while <see cref="InitializeAsync"/> is adopting the saved line parameters.
    /// </summary>
    /// <remarks>
    /// Guards the change handlers below so a startup read is not reported as a user edit that would
    /// affect already-open ports. Same shape as <c>_skipTuningEnabledPersistence</c>.
    /// </remarks>
    private bool _isLoadingSerialParameters;

    /// <summary>
    /// Text encoding a newly opened port starts with. Each port also has its own remembered value
    /// (<c>PortEncoding_&lt;port&gt;</c>), so re-opening one restores what it was last using.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineParametersSummary))]
    private string _textEncodingName = SerialEncodings.Utf8Name;

    /// <summary>Encoding choices for the sidebar picker.</summary>
    public IReadOnlyList<string> AvailableTextEncodings { get; } = SerialEncodings.SupportedNames;

    /// <summary>
    /// The line parameters in the classic shorthand — <c>8-N-1</c> — for the sidebar's 「高级参数」 header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flow control and encoding are appended only when they deviate from the shipped defaults, so the
    /// common case stays three characters wide while a non-default port cannot hide behind a summary that
    /// reads exactly like an untouched one.
    /// </para>
    /// <para>
    /// A read-only property rather than a converter: the value is a function of five properties at once,
    /// and a converter is only ever told about the one it is bound to.
    /// </para>
    /// </remarks>
    public string LineParametersSummary
    {
        get
        {
            var parity = Parity switch
            {
                System.IO.Ports.Parity.None => "N",
                System.IO.Ports.Parity.Odd => "O",
                System.IO.Ports.Parity.Even => "E",
                System.IO.Ports.Parity.Mark => "M",
                System.IO.Ports.Parity.Space => "S",
                _ => "?",
            };

            var stopBits = StopBitsOptions.FirstOrDefault(option => option.Value == StopBits)?.Name ?? "1";
            var summary = $"{DataBits}-{parity}-{stopBits}";

            if (Handshake != System.IO.Ports.Handshake.None)
            {
                var handshake = HandshakeOptions.FirstOrDefault(option => option.Value == Handshake)?.Name
                                ?? Handshake.ToString();
                summary = $"{summary} · {handshake}";
            }

            // "UTF-8" is the default and is therefore left out; anything else is spelled out, because a
            // GB18030 port showing Latin text where Chinese was expected is exactly the kind of thing a
            // folded section must not hide.
            if (!string.Equals(TextEncodingName, SerialEncodings.Utf8Name, StringComparison.OrdinalIgnoreCase))
            {
                summary = $"{summary} · {TextEncodingName}";
            }

            return summary;
        }
    }

    /// <summary>Settings key holding the encoding new ports start with.</summary>
    private const string SerialEncodingSettingKey = "SerialEncoding";

    /// <summary>Settings key holding one port's remembered encoding.</summary>
    private static string PortEncodingSettingKey(string portName) => $"PortEncoding_{portName}";

    public MainViewModel(
        ISerialPortService serialPortService,
        ITuningProtocolService tuningProtocolService,
        IFileLoggerService fileLoggerService,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger,
        IOutputPressureService outputPressure,
        INotificationService notifications,
        ISnippetService snippetService,
        IHighlightRuleService highlightRuleService,
        ILogExportService logExportService,
        ISerialPortDeviceEnumerator deviceEnumerator,
        IPortMetadataService portMetadataService,
        IPortPresetService portPresetService,
        Services.IBaudRateDetectorService? baudRateDetectorService = null,
        Services.IDataValidationService? dataValidationService = null)
    {
        _serialPortService = serialPortService;
        _tuningProtocolService = tuningProtocolService;
        _fileLoggerService = fileLoggerService;
        _settingsService = settingsService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _outputPressure = outputPressure;
        _notifications = notifications;
        _snippetService = snippetService;
        _highlightRuleService = highlightRuleService;
        _logExportService = logExportService;
        _baudRateDetectorService = baudRateDetectorService;
        _dataValidationService = dataValidationService;
        _deviceEnumerator = deviceEnumerator;
        _portMetadataService = portMetadataService;
        _portPresetService = portPresetService;

        // Keep _portsByName in sync with OpenPorts so the hot data-receive path can do O(1)
        // lookups instead of LINQ scans. OpenPorts itself is the source of truth for the UI.
        OpenPorts.CollectionChanged += OnOpenPortsChanged;

        // The history panel's empty state and its "清空历史" affordance follow the collection.
        RecentSearchTexts.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentSearches));

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

        // The quick-send library is independent of the serial configuration InitializeAsync reads, so
        // it loads on its own task instead of being appended to that chain. It is exception-safe by
        // construction — see InitializeSnippetsAsync — which is what makes fire-and-forget safe here.
        _ = InitializeSnippetsAsync();
        _ = InitializeHighlightRulesAsync();
        _ = InitializePortPresetsAsync();
    }

    private async Task InitializeAsync()
    {
        // Load saved baud rate settings
        BaudRate = await _settingsService.LoadSettingAsync("BaudRate", 3000000);
        UseCustomBaudRate = await _settingsService.LoadSettingAsync("UseCustomBaudRate", 0) == 1;
        CustomBaudRate = await _settingsService.LoadSettingAsync("CustomBaudRate", string.Empty);
        _logger.LogInformation("Loaded baud rate settings: BaudRate={BaudRate}, UseCustom={UseCustom}, CustomValue={CustomValue}",
            BaudRate, UseCustomBaudRate, CustomBaudRate);

        // Line parameters. Read under a suppression flag so a startup read cannot be reported as a user
        // edit (see HintThatLineParametersApplyOnNextOpen), and validated because the values round-trip
        // through settings.json as plain ints — a hand-edited file could name a value the enum does not
        // define, or StopBits.None, which SerialPort refuses at open time.
        _isLoadingSerialParameters = true;
        try
        {
            var dataBits = await _settingsService.LoadSettingAsync("DataBits", 8);
            DataBits = AvailableDataBits.Contains(dataBits) ? dataBits : 8;

            StopBits = PickAllowedValue(
                await _settingsService.LoadSettingAsync("StopBits", (int)System.IO.Ports.StopBits.One),
                StopBitsOptions,
                System.IO.Ports.StopBits.One);

            Parity = PickAllowedValue(
                await _settingsService.LoadSettingAsync("Parity", (int)System.IO.Ports.Parity.None),
                ParityOptions,
                System.IO.Ports.Parity.None);

            Handshake = PickAllowedValue(
                await _settingsService.LoadSettingAsync("Handshake", (int)System.IO.Ports.Handshake.None),
                HandshakeOptions,
                System.IO.Ports.Handshake.None);

            // Normalize even the default, so a settings.json holding "gb18030" or "GB-18030" resolves to
            // the canonical spelling before it is ever written back or shown in the picker.
            TextEncodingName = SerialEncodings.Normalize(
                await _settingsService.LoadSettingAsync(SerialEncodingSettingKey, SerialEncodings.Utf8Name));
        }
        finally
        {
            _isLoadingSerialParameters = false;
        }

        // Load send settings
        SendAsHex = await _settingsService.LoadSettingAsync("SendAsHex", 0) == 1;
        SendText = await _settingsService.LoadSettingAsync("SendText", string.Empty);
        ShowSentData = await _settingsService.LoadSettingAsync("ShowSentData", 1) == 1;
        SendTerminator = PickAllowedValue(
            await _settingsService.LoadSettingAsync("SendTerminator", (int)SendLineEnding.None),
            SendTerminatorOptions,
            SendLineEnding.None);
        SplitMultilineSend = await _settingsService.LoadSettingAsync("SplitMultilineSend", 1) == 1;
        await LoadSendLineDelayAsync();
        await LoadRecentSendTextsAsync();

        // Automation switches. Both are off unless the user turned them on, and both are adopted through
        // the property (rather than the field) under the same suppression flag the other line parameters
        // use, so a startup read is neither mistaken for a change nor written straight back. Assigning the
        // field would leave the bound checkbox showing the default while the value underneath is different.
        _isLoadingSerialParameters = true;
        try
        {
            RestoreLastPortsOnStartup =
                await _settingsService.LoadSettingAsync(SessionRestoreEnabledSettingKey, 0) == 1;
            AutoInitSequenceEnabled =
                await _settingsService.LoadSettingAsync(AutoInitEnabledSettingKey, 0) == 1;
            AutoInitSequence =
                await _settingsService.LoadSettingAsync(AutoInitScriptSettingKey, string.Empty);
        }
        finally
        {
            _isLoadingSerialParameters = false;
        }
        TxColorHex = await _settingsService.LoadSettingAsync("TxColorHex", PortColorPalette.DefaultTxHex);
        RxColorHex = await _settingsService.LoadSettingAsync("RxColorHex", PortColorPalette.DefaultRxHex);
        IsSidebarCollapsed = await _settingsService.LoadSettingAsync(SidebarCollapsedSettingKey, 0) == 1;
        IsAdvancedParametersExpanded =
            await _settingsService.LoadSettingAsync(AdvancedParametersExpandedSettingKey, 0) == 1;

        // Clamped rather than trusted: a hand-edited settings.json naming 4 or 400 must not produce a log that
        // cannot be read or a row taller than the viewport. Through the same helper the setter uses, so the two
        // cannot disagree about the range.
        LogFontSize = LogFontScale.Clamp(
            await _settingsService.LoadSettingAsync(LogFontSizeSettingKey, (int)LogFontScale.Default));

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

        // Load the persisted search mode / case-sensitivity switches first, so the history below is
        // restored against an already-correct UI state.
        await LoadSearchOptionsAsync();

        // Load recent search texts
        await LoadRecentSearchesAsync();

        // Scan ports
        await ScanPortsAsync();

        // After the scan rather than before it: whether a remembered port is still plugged in is exactly
        // what the scan answers, and restoring before that would attempt opens blindly.
        await RestorePreviousSessionAsync();

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

            // Device identity and the user's own notes are read once per scan rather than once per row:
            // both decorate every row equally, and a list of twenty ports should not mean twenty registry
            // walks. Neither can fail the scan — see the interface comments.
            var devices = await _deviceEnumerator.EnumerateAsync();
            var metadata = await _portMetadataService.LoadAsync();

            AvailablePorts.Clear();
            foreach (var portName in ports)
            {
                var item = new AvailablePortItem(portName);

                if (devices.TryGetValue(portName, out var device))
                {
                    item.ApplyDeviceInfo(device);
                }

                if (metadata.TryGetValue(portName, out var note))
                {
                    item.ApplyMetadata(note);
                }

                AvailablePorts.Add(item);
            }

            // Published for the 可用串口 card header. Set here rather than in a collection hook
            // because this method is the only writer of AvailablePorts.
            AvailablePortCount = AvailablePorts.Count;

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
                Parity = Parity,
                Handshake = Handshake,
                TextEncodingName = await ResolvePortEncodingForOpenAsync(portName)
            };

            // Sampled before the open and compared after it: 全部关闭 pressed while this port was
            // still being opened must not be undone by the port arriving a second later.
            var closeAllEpoch = Volatile.Read(ref _closeAllEpoch);

            // The auto-init sequence is armed *here*, before the open call, rather than when the Connected
            // state change arrives: the service raises Connected from inside OpenPortAsync, so anything set
            // after that await would always be too late for the first — and only — send this arms.
            if (AutoInitSequenceEnabled && AutoInitSequence.Trim().Length > 0)
            {
                _autoInitArmed[portName] = 1;
            }

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

                // Remembered for the startup restore, which replays the parameters this port actually
                // opened with rather than whatever the sidebar happens to say later.
                _openPortConfigs[portName] = config.Clone();
                _ = PersistSessionRestoreAsync();

                // Save port color and baud rate settings for next time
                SavePortColor(portName, portColor);
                await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
                await _settingsService.SaveSettingAsync("UseCustomBaudRate", UseCustomBaudRate ? 1 : 0);
                await _settingsService.SaveSettingAsync("CustomBaudRate", CustomBaudRate);
                await SaveLineParametersAsync();
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
            // 1.5 stop bits is legal in the API but not supported by every UART/driver combination, and the
            // failure surfaces as a generic IOException/ArgumentException. Without the hint there is no way
            // to connect "I changed one dropdown" to "the port will not open".
            StatusMessage = StopBits == System.IO.Ports.StopBits.OnePointFive
                ? $"Error opening port {portName}: {ex.Message} — 1.5 停止位可能不被设备/驱动支持，请改回 1 或 2 后重试"
                : $"Error opening port {portName}: {ex.Message}";
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

            // Remembered encodings are read *before* the batch starts: the service registers a port's
            // encoding while it opens it, so a value read afterwards would arrive after the first chunks
            // had already been decoded with the wrong one. A port that appears between the scan and the
            // batch falls back to the default — the same thing that happens for a port never configured.
            var encodingsByPort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in AvailablePorts)
            {
                encodingsByPort[candidate.PortName] = await ResolvePortEncodingForOpenAsync(candidate.PortName);
            }

            var defaultConfig = new SerialPortConfig
            {
                BaudRate = baudRateToUse,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity,
                Handshake = Handshake,
                TextEncodingName = TextEncodingName
            };

            // Sampled before the batch and compared after it: 全部关闭 pressed while this loop was
            // still opening ports must not be undone by those ports arriving afterwards.
            var closeAllEpoch = Volatile.Read(ref _closeAllEpoch);

            // What was already open before the batch, so the rollback below touches only the ports
            // this batch brought up.
            var alreadyOpen = _serialPortService.GetOpenPorts()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var openedCount = await _serialPortService.OpenAllPortsAsync(
                defaultConfig,
                portName => encodingsByPort.TryGetValue(portName, out var encoding)
                    ? encoding
                    : TextEncodingName);

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

            StatusMessage = openedCount == 0 && StopBits == System.IO.Ports.StopBits.OnePointFive
                ? $"Opened {openedCount} port(s) successfully (BaudRate: {baudRateToUse}) — 1.5 停止位可能不被设备/驱动支持，请改回 1 或 2 后重试"
                : $"Opened {openedCount} port(s) successfully (BaudRate: {baudRateToUse})";
            _logger.LogInformation("Batch opened {Count} ports with baud rate {BaudRate}", openedCount, baudRateToUse);

            // Save baud rate settings for next time
            await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
            await _settingsService.SaveSettingAsync("UseCustomBaudRate", UseCustomBaudRate ? 1 : 0);
            await _settingsService.SaveSettingAsync("CustomBaudRate", CustomBaudRate);
            await SaveLineParametersAsync();
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

            // Nothing is open, so there is nothing for the startup restore to replay. Cleared here rather
            // than per-port below because ClosePortAsync already handles the single-port case.
            _openPortConfigs.Clear();
            _autoInitArmed.Clear();
            _ = PersistSessionRestoreAsync();

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
    /// rebuild of DisplayLogs (two HashSet passes plus the matcher sweep over AllLogs, ~2000 entries),
    /// and it used to be invoked synchronously from three separate UI handlers — the search dropdown
    /// selection, the dropdown closing, and the Enter key — each of which had already assigned
    /// <see cref="SearchText"/> and therefore already armed the debounce. The result was up to four
    /// full rebuilds per keystroke or selection.
    ///
    /// Typing no longer reaches this method at all (it only writes <see cref="SearchDraft"/>); the
    /// debounce is kept because <see cref="SearchText"/> can also change twice in a row — switching a
    /// search-mode toggle re-validates and re-arms it immediately after a commit.
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
            // fresh timer — and its internal wait-handle bookkeeping — on every call.
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
        else
        {
            var matcher = GetOrCreateSearchMatcher(SearchText, IsRegexSearch, IsCaseSensitiveSearch);

            if (!matcher.IsValid)
            {
                // Only reachable in regex mode, with a pattern that does not parse. Show nothing
                // rather than silently ignoring the query — the inline error beside the box says why.
                filtered = new List<LogEntry>();
                MatchCount = 0;
            }
            else
            {
                filtered = AllLogs
                    .Where(log => matcher.IsMatch(log.Content) || matcher.IsMatch(log.PortName))
                    .ToList();
                MatchCount = filtered.Count;
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

        // A committed search rebuilds the whole list, so there is no scroll position worth
        // preserving and no history being read: re-attach the follow (and let the trim catch up).
        IsLogPinnedToBottom = true;
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

    #region Quick-send library

    /// <summary>Heading used for snippets that were saved without a group.</summary>
    private const string UngroupedSnippetGroupName = "未分组";

    /// <summary>Longest label accepted when one is derived from a payload instead of supplied.</summary>
    private const int DerivedSnippetLabelMaxLength = 40;

    /// <summary>
    /// The library in storage order. Mutated only on the UI thread; <see cref="SnippetGroups"/> is the
    /// grouped projection the flyout binds to.
    /// </summary>
    private readonly List<SendSnippet> _snippets = new();

    /// <summary>
    /// Quick-send library, grouped and ordered for display. Rebuilt wholesale after every change.
    /// </summary>
    public ObservableCollection<SnippetGroup> SnippetGroups { get; } = new();

    /// <summary>
    /// The library as a flat list, in storage order, for the command palette to search.
    /// </summary>
    /// <remarks>
    /// The palette searches this rather than <see cref="SnippetGroups"/>: groups exist to give the flyout
    /// headings, and flattening them back out to filter would be work for nothing. Both are projections of
    /// the same <c>_snippets</c>, so neither can drift from the other.
    /// </remarks>
    public IReadOnlyList<SendSnippet> AllSnippets => _snippets;

    /// <summary>Drives the quick-send flyout's empty state.</summary>
    public bool HasSnippets => SnippetGroups.Count > 0;

    /// <summary>
    /// Loads the library on the constructor's behalf.
    /// </summary>
    /// <remarks>
    /// Exception-safe by construction, because it is started fire-and-forget from the constructor and
    /// an escaping exception there would be unobserved. The rebuild is marshalled explicitly:
    /// <see cref="SnippetGroups"/> is bound to the UI, and this method is not guaranteed to resume on
    /// the UI thread.
    /// </remarks>
    private async Task InitializeSnippetsAsync()
    {
        try
        {
            var loaded = await _snippetService.LoadAsync();

            RunOnUiThread(() =>
            {
                _snippets.Clear();
                _snippets.AddRange(loaded);
                RebuildSnippetGroups();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise the quick-send library");
        }
    }

    /// <summary>
    /// Re-projects <see cref="_snippets"/> into the grouped, ordered view the flyout binds to.
    /// </summary>
    /// <remarks>
    /// Rebuilt wholesale rather than patched incrementally. The library is a handful of rows edited by
    /// hand, and a wholesale rebuild is what makes the headings correct for free: adding into a group,
    /// removing a group's last member and renaming a group all become the same operation here.
    /// </remarks>
    private void RebuildSnippetGroups()
    {
        SnippetGroups.Clear();

        var ordered = _snippets
            .GroupBy(snippet => snippet.Group ?? string.Empty)
            // Ungrouped first, then alphabetically: the ungrouped bucket is where an ad-hoc "save what
            // is in the send box" lands, and it should be the easiest thing to reach.
            .OrderBy(group => group.Key.Length == 0 ? 0 : 1)
            .ThenBy(group => group.Key, StringComparer.CurrentCulture);

        foreach (var group in ordered)
        {
            var items = group
                .OrderBy(snippet => snippet.Sort)
                .ThenBy(snippet => snippet.Label, StringComparer.CurrentCulture)
                .ToList();

            var name = group.Key.Length == 0 ? UngroupedSnippetGroupName : group.Key;
            SnippetGroups.Add(new SnippetGroup(name, items));
        }

        OnPropertyChanged(nameof(HasSnippets));
    }

    /// <summary>Adds a snippet, persists the library and confirms the addition to the user.</summary>
    public async Task AddSnippetAsync(SendSnippet snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet.Content))
        {
            StatusMessage = "快捷指令内容不能为空";
            return;
        }

        if (string.IsNullOrWhiteSpace(snippet.Id))
        {
            snippet.Id = Guid.NewGuid().ToString("N");
        }

        snippet.Group ??= string.Empty;
        snippet.Label = string.IsNullOrWhiteSpace(snippet.Label)
            ? DeriveSnippetLabel(snippet.Content)
            : snippet.Label.Trim();

        // Appended rather than inserted at a computed position: Sort only has to be monotonic, and
        // reusing max+1 keeps a later re-order from silently colliding with an existing value.
        snippet.Sort = _snippets.Count == 0 ? 0 : _snippets.Max(existing => existing.Sort) + 1;

        _snippets.Add(snippet);
        RebuildSnippetGroups();
        await PersistSnippetsAsync();

        _notifications.Notify($"已保存快捷指令「{snippet.Label}」", InfoBarSeverity.Success);
    }

    /// <summary>Removes a snippet and persists the library.</summary>
    public async Task RemoveSnippetAsync(SendSnippet snippet)
    {
        if (!_snippets.Remove(snippet))
        {
            return;
        }

        RebuildSnippetGroups();
        await PersistSnippetsAsync();
    }

    /// <summary>
    /// Sends one saved command to every open port, expanding its payload first when it opted in.
    /// </summary>
    public async Task SendSnippetAsync(SendSnippet snippet)
    {
        var payload = snippet.UseVariables
            ? _snippetService.ExpandVariables(snippet.Content, DateTimeOffset.Now)
            : snippet.Content;

        await SendPayloadAsync(payload, snippet.IsHex);
    }

    /// <summary>Collapses a payload into a one-line label, for a snippet saved without one.</summary>
    private static string DeriveSnippetLabel(string content)
    {
        var flattened = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flattened.Length == 0)
        {
            return "(未命名)";
        }

        return flattened.Length <= DerivedSnippetLabelMaxLength
            ? flattened
            : flattened[..DerivedSnippetLabelMaxLength] + "…";
    }

    private async Task PersistSnippetsAsync()
    {
        try
        {
            await _snippetService.SaveAsync(_snippets);
        }
        catch (Exception ex)
        {
            // The in-memory library stays correct, so the feature keeps working for this session and
            // the next edit retries. Only durability failed, and saying so is better than silence.
            _logger.LogError(ex, "Failed to persist the quick-send library");
            StatusMessage = "快捷指令保存失败，请检查设置文件是否可写";
        }
    }

    #endregion

    #region Keyword highlighting

    /// <summary>Rules in evaluation order; earlier rules win when ranges overlap.</summary>
    private readonly List<HighlightRule> _highlightRules = new();

    private IHighlightMatcher? _highlightMatcher;
    private bool _isHighlightSuppressed;

    /// <summary>Rules shown in the 高亮 flyout, in evaluation order.</summary>
    public ObservableCollection<HighlightRule> HighlightRules { get; } = new();

    /// <summary>Drives the 高亮 flyout's empty state.</summary>
    public bool HasHighlightRules => HighlightRules.Count > 0;

    /// <summary>
    /// The compiled snapshot handed to <c>LogListView</c>, or null when there is nothing to paint.
    /// </summary>
    /// <remarks>
    /// Rebuilt — never mutated — on every rule edit and on every appearance switch, because the
    /// resolved colours come from the palette. The snapshot carries a generation, which is what lets
    /// each entry's match cache invalidate itself on sight.
    /// </remarks>
    public IHighlightMatcher? HighlightMatcher
    {
        get => _highlightMatcher;
        private set => SetProperty(ref _highlightMatcher, value);
    }

    /// <summary>
    /// True while the receive path is under output pressure, in which case no row is decorated.
    /// </summary>
    /// <remarks>
    /// Highlighting is the most expensive per-line work the view does, so it is the first thing to
    /// drop under load — and because the matcher is an immutable snapshot rather than mutable state,
    /// dropping it is nothing more than not asking it for matches. Nothing has to be rebuilt when the
    /// flood passes.
    /// </remarks>
    public bool IsHighlightSuppressed
    {
        get => _isHighlightSuppressed;
        private set => SetProperty(ref _isHighlightSuppressed, value);
    }

    private async Task InitializeHighlightRulesAsync()
    {
        try
        {
            var loaded = await _highlightRuleService.LoadAsync();

            RunOnUiThread(() =>
            {
                _highlightRules.Clear();
                _highlightRules.AddRange(loaded);
                RebuildHighlightRules();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise the highlight rules");
        }
    }

    /// <summary>Re-projects the rule list and recompiles the snapshot.</summary>
    /// <remarks>
    /// Compilation is the only thing that ever bumps the generation, so it is deliberately the single
    /// place that does it — every cache in the log view is keyed on that number.
    /// </remarks>
    private void RebuildHighlightRules()
    {
        HighlightRules.Clear();
        foreach (var rule in _highlightRules)
        {
            // Refreshed here rather than in the list template: the template only ever sees the rule,
            // and the palette resolution needs the active appearance, which lives on this ViewModel.
            rule.RenderedColorHex = _highlightRuleService.ResolveColorHex(
                string.IsNullOrEmpty(rule.ColorHex) ? PortColorPalette.Slots[0].SlotHex : rule.ColorHex,
                _appliedIsDark);

            HighlightRules.Add(rule);
        }

        OnPropertyChanged(nameof(HasHighlightRules));

        HighlightMatcher = _highlightRules.Count == 0
            ? null
            : _highlightRuleService.CreateMatcher(_highlightRules, _appliedIsDark);
    }

    /// <summary>Adds a rule, recompiles and persists. Rejects a pattern that cannot work.</summary>
    public async Task AddHighlightRuleAsync(HighlightRule rule)
    {
        var error = _highlightRuleService.Validate(rule);
        if (error is not null)
        {
            // Rejected where it can be explained, instead of being compiled into a no-op that looks
            // like a broken feature.
            StatusMessage = error;
            return;
        }

        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            rule.Id = Guid.NewGuid().ToString("N");
        }

        rule.Sort = _highlightRules.Count == 0 ? 0 : _highlightRules.Max(existing => existing.Sort) + 1;

        _highlightRules.Add(rule);
        RebuildHighlightRules();
        await PersistHighlightRulesAsync();
    }

    /// <summary>Removes a rule, recompiles and persists.</summary>
    public async Task RemoveHighlightRuleAsync(HighlightRule rule)
    {
        if (!_highlightRules.Remove(rule))
        {
            return;
        }

        RebuildHighlightRules();
        await PersistHighlightRulesAsync();
    }

    /// <summary>Enables or disables a rule in place, then recompiles and persists.</summary>
    public async Task SetHighlightRuleEnabledAsync(HighlightRule rule, bool enabled)
    {
        if (rule.Enabled == enabled)
        {
            return;
        }

        rule.Enabled = enabled;
        RebuildHighlightRules();
        await PersistHighlightRulesAsync();
    }

    private async Task PersistHighlightRulesAsync()
    {
        try
        {
            await _highlightRuleService.SaveAsync(_highlightRules);
        }
        catch (Exception ex)
        {
            // The in-memory rules stay live, so highlighting keeps working for this session and the
            // next edit retries. Only durability failed, and saying so beats silence.
            _logger.LogError(ex, "Failed to persist the highlight rules");
            StatusMessage = "高亮规则保存失败，请检查设置文件是否可写";
        }
    }

    #endregion

    #region Log export

    /// <summary>
    /// Writes a snapshot of log entries to a file the user picked.
    /// </summary>
    /// <remarks>
    /// The entries arrive already materialised because the display buffer may only be enumerated on the
    /// UI thread while the write must not be; the window snapshots, this formats and persists. The scope
    /// label is passed in rather than inferred so the header describes what the user actually chose to
    /// export.
    /// </remarks>
    public async Task ExportLogsAsync(IReadOnlyList<LogEntry> entries, string filePath, string scopeLabel)
    {
        if (entries.Count == 0)
        {
            StatusMessage = "没有可导出的日志";
            return;
        }

        // Say "working" before the await: a large buffer takes long enough that a silent window reads
        // as a frozen one.
        StatusMessage = $"正在导出 {entries.Count} 行…";

        var result = await _logExportService.ExportAsync(
            new LogExportRequest(entries, filePath, scopeLabel, BuildSearchSummary()));

        StatusMessage = result.Succeeded
            ? $"已导出 {result.LineCount} 行到 {Path.GetFileName(filePath)}"
            : result.ErrorMessage!;
    }

    /// <summary>
    /// The search box's contents for the export header, or empty when it is not filtering anything.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to the search box. The level and port filters are applied before entries
    /// reach the display buffer, so anything more would be describing state this snapshot cannot see —
    /// and a header that is confidently wrong is worse than a short one.
    /// </remarks>
    private string BuildSearchSummary()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return string.Empty;
        }

        return IsRegexSearch ? $"\"{SearchText}\"（正则）" : $"\"{SearchText}\"";
    }

    #endregion

    /// <summary>
    /// ↑：往历史里更早的一条走。
    /// </summary>
    /// <returns>要显示在发送框里的内容；没有历史时返回 null。</returns>
    /// <remarks>
    /// Shell-style recall: the first ↑ of a walk stashes whatever was typed, so ↓ can hand it back instead
    /// of stranding the user on a history entry they did not want.
    /// </remarks>
    public string? RecallOlderSendText()
    {
        if (_recentSendTexts.Count == 0)
        {
            return null;
        }

        if (_sendHistoryIndex < 0)
        {
            _sendHistoryDraft = SendText;
            _sendHistoryIndex = 0;
        }
        else
        {
            _sendHistoryIndex = Math.Min(_sendHistoryIndex + 1, _recentSendTexts.Count - 1);
        }

        return ApplySendHistoryRecall(_recentSendTexts[_sendHistoryIndex]);
    }

    /// <summary>
    /// ↓：往最新的一条走，走过最新一条就恢复开始召回前的内容。
    /// </summary>
    /// <returns>要显示在发送框里的内容；当前没有在召回时返回 null（此时 ↓ 只是普通的方向键）。</returns>
    public string? RecallNewerSendText()
    {
        if (_sendHistoryIndex < 0)
        {
            return null;
        }

        _sendHistoryIndex--;
        if (_sendHistoryIndex < 0)
        {
            var draft = _sendHistoryDraft ?? string.Empty;
            _sendHistoryDraft = null;
            return ApplySendHistoryRecall(draft);
        }

        return ApplySendHistoryRecall(_recentSendTexts[_sendHistoryIndex]);
    }

    /// <summary>结束当前召回，使下一次 ↑ 从最新一条重新开始。</summary>
    public void ResetSendHistoryNavigation()
    {
        _sendHistoryIndex = -1;
        _sendHistoryDraft = null;
    }

    private string ApplySendHistoryRecall(string text)
    {
        _isRecallingSendHistory = true;
        try
        {
            SendText = text;
        }
        finally
        {
            _isRecallingSendHistory = false;
        }

        return text;
    }

    private async Task LoadRecentSendTextsAsync()
    {
        var json = await _settingsService.LoadSettingAsync("RecentSendTexts", string.Empty);

        if (!SendHistory.TryDeserialize(json, out var entries) && !string.IsNullOrWhiteSpace(json))
        {
            // Recoverable by design: the recall list is a convenience, so a damaged value costs the list
            // and not the session. Logged once, and never with the contents.
            _logger.LogWarning("Ignoring an unreadable send history from settings; starting with an empty list");
        }

        _recentSendTexts.Clear();
        _recentSendTexts.AddRange(entries);
    }

    private Task SaveRecentSendTextsAsync()
        => _settingsService.SaveSettingAsync("RecentSendTexts", SendHistory.Serialize(_recentSendTexts));

    /// <summary>
    /// Records a payload in the send history, newest first.
    /// </summary>
    /// <remarks>
    /// Called only once at least one port accepted the send, so ↑ offers what actually went out rather than
    /// what was attempted. A hex payload is recorded exactly as typed: the hex switch belongs to the user,
    /// and recalling a payload only to have it re-interpreted differently would be worse than not offering
    /// it at all.
    /// </remarks>
    private void RecordSendHistory(string payload)
    {
        var merged = SendHistory.Merge(_recentSendTexts, payload);

        _recentSendTexts.Clear();
        _recentSendTexts.AddRange(merged);

        ResetSendHistoryNavigation();
        _ = SaveRecentSendTextsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (string.IsNullOrEmpty(SendText))
            return;

        await SendPayloadAsync(SendText, SendAsHex);
    }

    /// <summary>
    /// Broadcasts one payload to every open port and reports the outcome through the status bar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="SendAsync"/> in v2.2.4 so the quick-send library travels exactly the
    /// same path as the send box — same broadcast, same hex parsing and error text, same sent-log
    /// entry, same statistics refresh. A second send implementation would inevitably drift from this
    /// one. It is deliberately not gated on <c>SendCommand.IsRunning</c>: that guard belongs to the
    /// command's <c>CanExecute</c>, and concurrent writes are serialised per port by the service.
    /// </para>
    /// <para>
    /// Two things are decided here rather than per caller. The payload is encoded <b>per port</b>, because
    /// two open ports can be configured for two character sets and one send has to serve both — the text is
    /// built once and only the encoding differs. And the line terminator is applied
    /// (<see cref="SendLineEndings"/>) before that encoding, so a device that expects <c>\r</c> can be
    /// driven from the single-line send box.
    /// </para>
    /// </remarks>
    private async Task SendPayloadAsync(string text, bool asHex)
    {
        if (OpenPorts.Count == 0)
        {
            StatusMessage = "请先打开一个串口";
            return;
        }

        // Targeting is applied here and nowhere else: the send box, the quick-send flyout and the F2 palette
        // all funnel through this method, so one filter keeps all three consistent. Tuning is deliberately not
        // included — SendTuningFileAsync has its own worker and its whole design is "broadcast to every open
        // port".
        var targetPorts = ResolveSendTargets();
        if (targetPorts.Count == 0)
        {
            // Unreachable while PruneSendTargets runs on every change to OpenPorts, but a send that silently
            // does nothing is worse than a message that should never appear.
            StatusMessage = "所选目标串口均已关闭，请重新选择发送目标";
            return;
        }

        try
        {
            Dictionary<string, byte[]> payloads;
            IReadOnlyList<string> displayLines;

            if (asHex)
            {
                if (!TryParseHexInput(text, out var bytes, out var hexError))
                {
                    StatusMessage = hexError!;
                    return;
                }

                // Hex is already bytes, so the same array goes to every port and the line-ending setting
                // deliberately does not apply: appending \r to a hand-built frame would corrupt it.
                payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var portName in targetPorts)
                {
                    payloads[portName] = bytes;
                }

                displayLines = new[] { $"[HEX] {BitConverter.ToString(bytes).Replace("-", " ")}" };
            }
            else
            {
                var plan = SendLineEndings.CreatePlan(text, SendTerminator, SplitMultilineSend);

                // F7: with a line interval set and more than one line to write, the lines go out one at a
                // time. The zero case never enters this branch, so an install that leaves the interval at
                // its default sends exactly what it always sent — see SendLinesWithDelayAsync.
                if (SendLineDelayMs > 0 && plan.Segments.Count > 1)
                {
                    if (await SendLinesWithDelayAsync(targetPorts, plan))
                    {
                        RecordSendHistory(text);
                    }

                    return;
                }

                // Encoded once per port, before any write starts: the encoding is read from live per-port
                // state, and a second read after the first send would be a chance to disagree with the
                // bytes already on the wire.
                payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var portName in targetPorts)
                {
                    payloads[portName] = SerialEncodings
                        .Resolve(_serialPortService.GetPortTextEncodingName(portName))
                        .GetBytes(plan.Payload);
                }

                displayLines = CapSentLogLines(plan.DisplayLines);
            }

            // The status line states a per-port size, matching the original wording. With per-port encodings
            // the sizes can differ (GB18030 and UTF-8 disagree on how many bytes "温度" is), and summing them
            // under "向 N 个串口发送 X 字节" would read as "X each" — a number that is wrong for every port.
            var bytesPerPort = payloads[targetPorts[0]].Length;
            var uniformPayloadSize = payloads.Values.All(bytes => bytes.Length == bytesPerPort);
            var targetNote = DescribeSendTargets(targetPorts);

            StatusMessage = (targetPorts.Count == 1
                ? $"正在发送 {bytesPerPort} 字节..."
                : uniformPayloadSize
                    ? $"正在向 {targetPorts.Count} 个串口发送 {bytesPerPort} 字节..."
                    : $"正在向 {targetPorts.Count} 个串口发送（各端口按自身编码，字节数不同）...")
                + targetNote;

            var sendResults = await SendDataToPortsAsync(targetPorts, portName => payloads[portName]);
            var successfulPorts = sendResults
                .Where(result => result.IsSuccess)
                .Select(result => result.PortName)
                .ToList();
            var failedResults = sendResults
                .Where(result => !result.IsSuccess)
                .ToList();

            if (ShowSentData && successfulPorts.Count > 0)
            {
                AddSentLogs(successfulPorts, displayLines);
            }

            RefreshPortStatistics(targetPorts);

            if (successfulPorts.Count > 0)
            {
                RecordSendHistory(text);
            }

            if (failedResults.Count > 0)
            {
                var failedPorts = string.Join(", ", failedResults.Select(result => result.PortName));
                StatusMessage = successfulPorts.Count == 0
                    ? $"发送失败: {failedPorts}"
                    : $"部分发送失败: 成功 {successfulPorts.Count}/{targetPorts.Count}，失败 {failedPorts}";
                return;
            }

            StatusMessage = (targetPorts.Count == 1
                ? $"已发送 {bytesPerPort} 字节"
                : uniformPayloadSize
                    ? $"已向 {targetPorts.Count} 个串口发送 {bytesPerPort} 字节"
                    : $"已向 {targetPorts.Count} 个串口发送（各端口按自身编码，字节数不同）")
                + targetNote;
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

    #region Port identity, notes, presets, automation and history (v2.5.0)

    // =============================================================================================
    // Fields and settings keys shared by the features below
    // =============================================================================================

    private readonly ISerialPortDeviceEnumerator _deviceEnumerator;
    private readonly IPortMetadataService _portMetadataService;
    private readonly IPortPresetService _portPresetService;

    /// <summary>Settings key holding whether the startup restore (F9) is on. Off by default.</summary>
    private const string SessionRestoreEnabledSettingKey = "SessionRestoreEnabled";

    /// <summary>Settings key holding the startup restore payload: port name → profile.</summary>
    private const string SessionRestorePayloadSettingKey = "SessionRestorePorts";

    /// <summary>Settings key holding the line-delay interval (F7).</summary>
    private const string SendLineDelaySettingKey = "SendLineDelayMs";

    /// <summary>Settings key holding whether an init sequence is sent after open (F4). Off by default.</summary>
    private const string AutoInitEnabledSettingKey = "AutoInitSequenceEnabled";

    /// <summary>Settings key holding that init sequence.</summary>
    private const string AutoInitScriptSettingKey = "AutoInitSequence";

    /// <summary>The parameters each currently open port actually opened with, for the startup restore.</summary>
    private readonly Dictionary<string, SerialPortConfig> _openPortConfigs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ports whose next <c>Connected</c> transition should run the init sequence. See F4 below.</summary>
    private readonly ConcurrentDictionary<string, byte> _autoInitArmed = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    // =============================================================================================
    // F6 — per-port notes / tags / group
    // =============================================================================================

    /// <summary>The metadata currently shown for one row, or null when the row has none.</summary>
    public PortMetadata? GetPortMetadata(string portName)
    {
        foreach (var item in AvailablePorts)
        {
            if (string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return new PortMetadata
                {
                    PortName = item.PortName,
                    Notes = item.Notes,
                    Tags = item.Tags,
                    Group = item.Group,
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 写入一个端口的备注 / 标签 / 分组，并就地更新列表里的那一行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row is patched in place rather than re-scanned, because a scan two seconds after saving a note
    /// is both slower and resets whatever the user was about to click — the note has to appear where they
    /// are already looking.
    /// </para>
    /// <para>
    /// Whole-set read-modify-write: <see cref="IPortMetadataService"/> persists one string key, and this
    /// runs on a button press, not on a hot path, so the extra read costs nothing measurable. The
    /// alternative (a per-port key) would leave orphaned entries behind for every machine ever plugged in.
    /// </para>
    /// </remarks>
    public async Task SavePortMetadataAsync(string portName, string notes, string tags, string group)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        var all = (await _portMetadataService.LoadAsync()).Values.ToList();
        var existing = all.FirstOrDefault(
            entry => string.Equals(entry.PortName, portName, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new PortMetadata { PortName = portName };
            all.Add(existing);
        }

        existing.Notes = notes;
        existing.Tags = tags;
        existing.Group = group;

        var sanitized = _portMetadataService.Sanitize(existing);

        // An entry whose every field is blank is a row the user cleared, so it is removed rather than kept
        // as a shell — otherwise the setting grows one entry per port ever edited and never shrinks.
        if (sanitized.IsEmpty)
        {
            all.Remove(existing);
            await _portMetadataService.SaveAsync(all);
        }
        else
        {
            await _portMetadataService.SaveAsync(all);
        }

        foreach (var item in AvailablePorts)
        {
            if (string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                item.ApplyMetadata(sanitized);
                break;
            }
        }

        StatusMessage = sanitized.IsEmpty
            ? $"已清除 {portName} 的备注"
            : $"已保存 {portName} 的备注";
    }

    // =============================================================================================
    // F3 — named port configuration presets
    // =============================================================================================

    /// <summary>The saved presets, oldest first, for the sidebar picker.</summary>
    public ObservableCollection<PortPreset> PortPresets { get; } = new();

    /// <summary>The preset selected in the sidebar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPortPreset))]
    private PortPreset? _selectedPortPreset;

    /// <summary>Gates the 套用 / 删除 buttons, so neither has to say "select one first" after the fact.</summary>
    public bool HasSelectedPortPreset => SelectedPortPreset is not null;

    private async Task InitializePortPresetsAsync()
    {
        try
        {
            var presets = await _portPresetService.LoadAsync();

            PortPresets.Clear();
            foreach (var preset in presets)
            {
                PortPresets.Add(preset);
            }
        }
        catch (Exception ex)
        {
            // The service is already exception-safe for its own failures; this catches the collection
            // being touched while the window is going away. Never worth failing the launch over.
            _logger.LogWarning(ex, "Could not load the port preset library");
        }
    }

    /// <summary>把侧栏当前的参数与行尾符抓成一份可保存的 profile。</summary>
    public SerialPortProfile CaptureCurrentProfile()
        => PortProfileMapper.FromConfig(
            new SerialPortConfig
            {
                BaudRate = BaudRate,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity,
                Handshake = Handshake,
                TextEncodingName = TextEncodingName,
            },
            SendTerminator);

    /// <summary>名字能否使用；返回 null 表示可以。</summary>
    public string? ValidatePortPresetName(string name, string? editingId)
        => _portPresetService.Validate(
            new PortPreset { Name = name, Id = editingId ?? string.Empty },
            PortPresets.ToList(),
            editingId);

    /// <summary>新增或覆盖一套档案。</summary>
    public async Task<bool> SavePortPresetAsync(string name, string? editingId = null)
    {
        var trimmed = (name ?? string.Empty).Trim();

        var error = ValidatePortPresetName(trimmed, editingId);
        if (error is not null)
        {
            StatusMessage = error;
            return false;
        }

        var existing = string.IsNullOrEmpty(editingId)
            ? null
            : PortPresets.FirstOrDefault(preset => string.Equals(preset.Id, editingId, StringComparison.Ordinal));

        var profile = CaptureCurrentProfile();

        if (existing is not null)
        {
            existing.Name = trimmed;
            existing.Profile = profile;
        }
        else
        {
            PortPresets.Add(_portPresetService.Create(profile, trimmed));
        }

        await _portPresetService.SaveAsync(PortPresets.ToList());
        SelectedPortPreset = existing ?? PortPresets.LastOrDefault();

        StatusMessage = $"已保存档案“{trimmed}”";
        return true;
    }

    /// <summary>
    /// 套用一套档案：把它的参数与行尾符写回侧栏。
    /// </summary>
    /// <remarks>
    /// Applied to the sidebar and not to live ports, which is the same rule every other line parameter
    /// follows (<see cref="HintThatLineParametersApplyOnNextOpen"/>): applying them to an open port means
    /// an implicit close/reopen, and that race is forbidden. The status line says so explicitly, because
    /// "nothing happened" is otherwise indistinguishable from "it worked".
    /// </remarks>
    public async Task ApplyPortPresetAsync(PortPreset? preset)
    {
        if (preset is null)
        {
            StatusMessage = "请先在档案列表里选一套配置";
            return;
        }

        var profile = SerialPortConfigCodec.Sanitize(preset.Profile);

        ApplyBaudRateToSidebar(profile.BaudRate);
        DataBits = profile.DataBits;
        StopBits = (System.IO.Ports.StopBits)profile.StopBits;
        Parity = (System.IO.Ports.Parity)profile.Parity;
        Handshake = (System.IO.Ports.Handshake)profile.Handshake;
        TextEncodingName = profile.TextEncodingName;

        // Across-field on purpose: a preset that changed the baud rate but left the terminator would be a
        // half-applied setup that looks applied while sending the wrong frame.
        SendTerminator = (SendLineEnding)profile.LineEnding;

        await PersistCurrentLineParametersAsync();

        StatusMessage = $"已套用档案“{preset.Name}”：这些参数在下次打开串口时生效";
    }

    public async Task DeletePortPresetAsync(PortPreset? preset)
    {
        if (preset is null)
        {
            StatusMessage = "请先选择要删除的档案";
            return;
        }

        PortPresets.Remove(preset);
        if (ReferenceEquals(SelectedPortPreset, preset))
        {
            SelectedPortPreset = PortPresets.LastOrDefault();
        }

        await _portPresetService.SaveAsync(PortPresets.ToList());

        StatusMessage = $"已删除档案“{preset.Name}”";
    }

    /// <summary>
    /// 把一个波特率放回侧栏；列表里没有的值走「自定义」，让 Combobox 仍然说得通。
    /// </summary>
    private void ApplyBaudRateToSidebar(int baudRate)
    {
        if (AvailableBaudRates.Contains(baudRate))
        {
            CustomBaudRate = string.Empty;
            UseCustomBaudRate = false;
            BaudRate = baudRate;
            return;
        }

        UseCustomBaudRate = true;
        CustomBaudRate = baudRate.ToString();
        BaudRate = baudRate;
    }

    /// <summary>把当前侧栏参数写回 settings。</summary>
    private async Task PersistCurrentLineParametersAsync()
    {
        await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
        await _settingsService.SaveSettingAsync("UseCustomBaudRate", UseCustomBaudRate ? 1 : 0);
        await _settingsService.SaveSettingAsync("CustomBaudRate", CustomBaudRate);
        await SaveLineParametersAsync();
        await _settingsService.SaveSettingAsync("SendTerminator", (int)SendTerminator);
    }

    // =============================================================================================
    // F9 — restore the previously open ports at startup
    // =============================================================================================

    /// <summary>启动时恢复上次打开的串口。默认关闭。</summary>
    [ObservableProperty]
    private bool _restoreLastPortsOnStartup;

    partial void OnRestoreLastPortsOnStartupChanged(bool value)
    {
        if (_isLoadingSerialParameters)
        {
            return;
        }

        _ = _settingsService.SaveSettingAsync(SessionRestoreEnabledSettingKey, value ? 1 : 0);
    }

    /// <summary>记录「当前打开的串口都用哪些参数」，供下次启动恢复。</summary>
    private async Task PersistSessionRestoreAsync()
    {
        if (!RestoreLastPortsOnStartup)
        {
            // Recording while the switch is off would mean enabling it restores ports from a session the
            // user never asked to remember.
            await _settingsService.SaveSettingAsync(SessionRestorePayloadSettingKey, string.Empty);
            return;
        }

        var map = new Dictionary<string, SerialPortProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _openPortConfigs)
        {
            map[pair.Key] = PortProfileMapper.FromConfig(pair.Value, SendTerminator);
        }

        await _settingsService.SaveSettingAsync(
            SessionRestorePayloadSettingKey,
            JsonSerializer.Serialize(map, ProfileJsonOptions));
    }

    /// <summary>
    /// 扫描之后尝试恢复上次打开的串口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs after <c>ScanPortsAsync</c> rather than in the constructor because "is the port still there"
    /// is the only question that matters here, and the scan is what answers it.
    /// </para>
    /// <para>
    /// Every failure is per-port and non-fatal. Same rule as <c>SnippetService.LoadAsync</c>: a convenience
    /// feature must not be able to stop the app from starting, and a machine whose USB hub was unplugged
    /// overnight is the expected case rather than the broken one.
    /// </para>
    /// </remarks>
    private async Task RestorePreviousSessionAsync()
    {
        if (!RestoreLastPortsOnStartup)
        {
            return;
        }

        string raw;
        try
        {
            raw = await _settingsService.LoadSettingAsync(SessionRestorePayloadSettingKey, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the session restore payload");
            return;
        }

        if (!SerialPortConfigCodec.TryParseMap<SerialPortProfile>(raw, ProfileJsonOptions, out var saved))
        {
            return;
        }

        if (saved.Count == 0)
        {
            return;
        }

        var restored = 0;
        var skipped = 0;

        foreach (var pair in saved)
        {
            try
            {
                var profile = SerialPortConfigCodec.Sanitize(pair.Value);

                // Per-field validation happens inside the codec; this is the one check that needs the live
                // machine: a port that no longer exists is skipped, not reported as a failure.
                if (!IsPortStillPresent(pair.Key))
                {
                    skipped++;
                    continue;
                }

                var config = PortProfileMapper.ToConfig(profile, pair.Key);
                var opened = await _serialPortService.OpenPortAsync(config);
                if (!opened)
                {
                    skipped++;
                    continue;
                }

                await AfterRestoredPortOpenedAsync(pair.Key, config);
                restored++;
            }
            catch (Exception ex)
            {
                skipped++;
                _logger.LogWarning(ex, "Could not restore port {PortName} from the previous session", pair.Key);
            }
        }

        StatusMessage = skipped == 0
            ? $"已恢复上次打开的 {restored} 个串口"
            : $"已恢复 {restored} 个串口，跳过 {skipped} 个（设备已拔下或打开失败）";
    }

    private bool IsPortStillPresent(string portName)
    {
        foreach (var item in AvailablePorts)
        {
            if (string.Equals(item.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把一个「直接由服务打开的」端口接进 MainWindow 的其余状态。
    /// </summary>
    /// <remarks>
    /// This is deliberately a small subset of <c>OpenPortAsync</c>: no baud-rate saving (nothing changed),
    /// no auto-init arming (restoring a session must not re-send anything), no statistics snapshot. The
    /// duplication that remains — file logging, colour, the row — is the minimum a port needs to behave
    /// like one the user opened.
    /// </remarks>
    private async Task AfterRestoredPortOpenedAsync(string portName, SerialPortConfig config)
    {
        await _fileLoggerService.StartLoggingAsync(portName);

        var portColor = await GetOrAssignPortColorAsync(portName);
        var portViewModel = new PortViewModel(portName, _serialPortService, _dispatcherQueue)
        {
            ColorHex = portColor
        };

        portViewModel.UpdateStatistics(_serialPortService.GetStatistics(portName));
        OpenPorts.Add(portViewModel);
        SavePortColor(portName, portColor);

        _serialPortService.SetPortTextEncoding(portName, config.TextEncodingName);
        _openPortConfigs[portName] = config.Clone();
    }

    // =============================================================================================
    // F4 — automatic init sequence after open
    // =============================================================================================

    /// <summary>
    /// 端口打开成功后自动发送的初始化序列。默认关闭。
    /// </summary>
    /// <remarks>
    /// Off by default because it writes to a device nobody asked it to write to. That is not a theoretical
    /// concern: plenty of bootloaders and configuration consoles interpret an unexpected command during
    /// their first second by switching modes, so a wrong script is not "a harmless extra line" but "the
    /// device is no longer doing what it was doing". The user has to turn this on.
    /// </remarks>
    [ObservableProperty]
    private bool _autoInitSequenceEnabled;

    /// <summary>初始化序列的内容（多行时逐行发送）。</summary>
    [ObservableProperty]
    private string _autoInitSequence = string.Empty;

    partial void OnAutoInitSequenceEnabledChanged(bool value)
    {
        if (_isLoadingSerialParameters)
        {
            return;
        }

        _ = _settingsService.SaveSettingAsync(AutoInitEnabledSettingKey, value ? 1 : 0);
    }

    partial void OnAutoInitSequenceChanged(string value)
    {
        if (_isLoadingSerialParameters)
        {
            return;
        }

        _ = _settingsService.SaveSettingAsync(AutoInitScriptSettingKey, value ?? string.Empty);
    }

    /// <summary>
    /// Connected 时判断要不要跑一次初始化序列。
    /// </summary>
    /// <remarks>
    /// <para>
    /// The armed flag is what keeps this from becoming a reconnect storm. <c>AutoReconnect</c> re-raises
    /// Connected every few seconds for a flaky cable, and each raise would otherwise replay the script — a
    /// device being re-initialised twenty times a minute while trying to recover. Only <c>OpenPortAsync</c>
    /// arms it, and the first Connected consumes it, so exactly one open produces exactly one sequence.
    /// </para>
    /// <para>
    /// 「全部打开」 deliberately does not arm anything: batch-opening every port on the machine and then
    /// writing an init script to each is the kind of automation that is very hard to undo.
    /// </para>
    /// </remarks>
    private void MaybeRunAutoInitSequence(string portName, Core.Enums.ConnectionState newState)
    {
        if (newState != Core.Enums.ConnectionState.Connected)
        {
            return;
        }

        if (!_autoInitArmed.TryRemove(portName, out _))
        {
            return;
        }

        _ = RunAutoInitSequenceAsync(portName);
    }

    /// <summary>
    /// How long to wait after the port comes up before writing anything.
    /// </summary>
    /// <remarks>
    /// Many devices need a moment after their serial line is asserted before a command on it means anything,
    /// and writing into that window is how the first line of a script gets swallowed.
    /// </remarks>
    private const int AutoInitLeadInMs = 150;

    private async Task RunAutoInitSequenceAsync(string portName)
    {
        try
        {
            await Task.Delay(AutoInitLeadInMs);

            if (!_portsByName.ContainsKey(portName))
            {
                // Closed (or never stayed open) during the wait: nothing to initialise.
                return;
            }

            var script = AutoInitSequence.Trim();
            if (script.Length == 0)
            {
                return;
            }

            // Same variable syntax as a snippet, and the same "unrecognised token stays verbatim" rule —
            // a typo must reach the device as typed, not disappear into the ether.
            var expanded = _snippetService.ExpandVariables(script, DateTimeOffset.Now);

            var plan = SendLineEndings.CreatePlan(expanded, SendTerminator, SplitMultilineSend);
            var targets = new[] { portName };

            if (SendLineDelayMs > 0 && plan.Segments.Count > 1)
            {
                await SendLinesWithDelayAsync(targets, plan);
            }
            else
            {
                await SendTextToTargetsAsync(targets, plan.Payload, plan.DisplayLines);
            }

            StatusMessage = $"Port {portName}: 已自动发送初始化序列";
        }
        catch (Exception ex)
        {
            // Not a drag-the-user-into-it failure: the port is open and usable either way.
            _logger.LogError(ex, "The auto init sequence failed on {PortName}", portName);
        }
    }

    // =============================================================================================
    // F7 — per-line interval for multi-line sends
    // =============================================================================================

    /// <summary>Upper bound on the line interval. Ten seconds between lines is already a timeout.</summary>
    public const int MaxLineDelayMs = 10_000;

    /// <summary>
    /// 行间隔输入框里的原文。真正生效的是 <see cref="SendLineDelayMs"/>。
    /// </summary>
    /// <remarks>
    /// A string rather than an int because WinUI's <c>NumberBox</c> binds <c>double</c> and an int property
    /// would either need an IValueConverter nobody asked for or silently fail to update. Parsing here keeps
    /// both directions honest and lets an unparseable value stay whatever the user typed instead of being
    /// rewritten under them mid-keystroke.
    /// </remarks>
    [ObservableProperty]
    private string _sendLineDelayText = "0";

    private bool _isLoadingSendSettings;

    /// <summary>行与行之间的等待毫秒数；0 表示整段一次写出（与没有这个功能时完全一致）。</summary>
    public int SendLineDelayMs { get; private set; }

    partial void OnSendLineDelayTextChanged(string value)
    {
        SendLineDelayMs = ParseLineDelay(value);
        OnPropertyChanged(nameof(SendLineDelayMs));

        if (!_isLoadingSendSettings)
        {
            _ = _settingsService.SaveSettingAsync(SendLineDelaySettingKey, SendLineDelayMs);
        }
    }

    private static int ParseLineDelay(string? value)
    {
        if (int.TryParse(value, out var parsed) && parsed >= 0)
        {
            return parsed > MaxLineDelayMs ? MaxLineDelayMs : parsed;
        }

        // Anything unparseable behaves as "no delay", which is existing behaviour rather than a surprising
        // one — and the clamped value is written back only on the next real edit.
        return 0;
    }

    /// <summary>Default delay when the switch first becomes reachable.</summary>
    private async Task LoadSendLineDelayAsync()
    {
        var stored = await _settingsService.LoadSettingAsync(SendLineDelaySettingKey, 0);

        _isLoadingSendSettings = true;
        try
        {
            SendLineDelayText = ParseLineDelay(stored.ToString()).ToString();
        }
        finally
        {
            _isLoadingSendSettings = false;
        }
    }

    /// <summary>
    /// 把一个已经切好的计划逐行写出，行间等待 <see cref="SendLineDelayMs"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Byte identity is the contract.</b> Concatenating the segments reproduces the single-write payload
    /// exactly (pinned by <c>SendLineEndingsTests</c>), so with the interval left at zero nothing here ever
    /// runs and an install that never opens the setting cannot change what reaches the device.
    /// </para>
    /// <para>
    /// <c>Task.Delay</c> rather than <c>Thread.Sleep</c>: this sits on the UI thread's continuation, and a
    /// blocking wait would freeze the whole window between lines. Nothing more elaborate is needed — the
    /// wait is re-evaluated each iteration because the target set can shrink while we wait.
    /// </para>
    /// </remarks>
    /// <returns>True when at least one line reached at least one port — what decides whether the payload is
    /// worth putting in the ↑ history, which only offers things that actually went out.</returns>
    private async Task<bool> SendLinesWithDelayAsync(
        IReadOnlyList<string> targetPorts,
        SerialPortTool.Helpers.SendPlan plan)
    {
        var written = 0;
        var anyAccepted = false;

        for (var index = 0; index < plan.Segments.Count; index++)
        {
            if (index > 0)
            {
                await Task.Delay(SendLineDelayMs);
            }

            // Re-evaluated every iteration rather than once: the whole point of the interval is that
            // something can happen between the lines, and the most likely thing is the port going away.
            var liveTargets = targetPorts.Where(name => _portsByName.ContainsKey(name)).ToList();
            if (liveTargets.Count == 0)
            {
                StatusMessage = written > 0
                    ? $"已发送 {written} 行；等待剩余行时目标串口已关闭"
                    : "目标串口已关闭，未发送";
                return anyAccepted;
            }

            if (await SendTextToTargetsAsync(
                    liveTargets, plan.Segments[index], new[] { plan.DisplayLines[index] }))
            {
                anyAccepted = true;
            }

            written++;
        }

        StatusMessage = written == 1
            ? "已发送 1 行"
            : $"已按 {SendLineDelayMs} ms 行间隔逐行发送 {written} 行";
        return anyAccepted;
    }

    /// <summary>
    /// 把一段已经算好的文本发给指定端口，并写入发送日志。
    /// </summary>
    /// <returns>True when at least one port accepted it.</returns>
    private async Task<bool> SendTextToTargetsAsync(
        IReadOnlyList<string> targetPorts,
        string payload,
        IReadOnlyList<string> displayLines)
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var portName in targetPorts)
        {
            payloads[portName] = SerialEncodings
                .Resolve(_serialPortService.GetPortTextEncodingName(portName))
                .GetBytes(payload);
        }

        var sendResults = await SendDataToPortsAsync(targetPorts, portName => payloads[portName]);
        var successfulPorts = sendResults
            .Where(result => result.IsSuccess)
            .Select(result => result.PortName)
            .ToList();

        if (ShowSentData && successfulPorts.Count > 0)
        {
            AddSentLogs(successfulPorts, CapSentLogLines(displayLines));
        }

        RefreshPortStatistics(targetPorts);

        return successfulPorts.Count > 0;
    }

    // =============================================================================================
    // F8 — searchable send history panel
    // =============================================================================================

    /// <summary>发送历史面板是否打开。它叠加在既有的 ↑/↓ 召回之上，不替换它。</summary>
    [ObservableProperty]
    private bool _isSendHistoryPanelOpen;

    /// <summary>
    /// 面板显示的历史列表：与 ↑/↓ 召回<b>同一份</b>数据。
    /// </summary>
    /// <remarks>
    /// Same source rather than a parallel one — a second list is how a history ends up disagreeing with
    /// itself ("I deleted it in the panel and ↑ still served it"). Rebuilt on open and after every change;
    /// fifty strings is not worth an ObservableCollection diffing protocol.
    /// </remarks>
    public ObservableCollection<string> SendHistoryEntries { get; } = new();

    /// <summary>History deferred until someone opens the panel, so the ↑/↓ recall has no competition.</summary>
    public void OpenSendHistoryPanel()
    {
        RefreshSendHistoryEntries();
        IsSendHistoryPanelOpen = true;
    }

    public void CloseSendHistoryPanel() => IsSendHistoryPanelOpen = false;

    private void RefreshSendHistoryEntries()
    {
        SendHistoryEntries.Clear();
        foreach (var entry in _recentSendTexts)
        {
            SendHistoryEntries.Add(entry);
        }

        OnPropertyChanged(nameof(HasSendHistory));
    }

    /// <summary>True when there is any history to show, so the panel can say why it is empty.</summary>
    public bool HasSendHistory => SendHistoryEntries.Count > 0;

    /// <summary>把一条历史放进发送框。</summary>
    public void UseSendHistoryEntry(string payload)
    {
        ApplySendHistoryRecall(payload);
        ResetSendHistoryNavigation();
        IsSendHistoryPanelOpen = false;
    }

    /// <summary>
    /// 删除单条历史。
    /// </summary>
    public async Task RemoveSendHistoryEntryAsync(string payload)
    {
        if (_recentSendTexts.RemoveAll(entry => string.Equals(entry, payload, StringComparison.Ordinal)) == 0)
        {
            return;
        }

        await SaveRecentSendTextsAsync();

        // The recall cursor indexes the same list, so deleting under it would make ↑ land on the wrong entry.
        ResetSendHistoryNavigation();
        RefreshSendHistoryEntries();
    }

    public async Task ClearSendHistoryAsync()
    {
        if (_recentSendTexts.Count == 0)
        {
            return;
        }

        _recentSendTexts.Clear();
        await SaveRecentSendTextsAsync();
        ResetSendHistoryNavigation();
        RefreshSendHistoryEntries();
    }

    #endregion

    #region Synthetic load (developer only)

    /// <summary>Ticks per second the generator runs at: 50 ms of work per tick, like a busy receive path.</summary>
    private const int FloodTicksPerSecond = 20;

    private const int MinFloodLinesPerSecond = 100;
    private const int MaxFloodLinesPerSecond = 200000;

    /// <summary>Rough bytes per generated line, for the pressure sampler.</summary>
    private const int SyntheticLoadBytesPerLine = 48;

    private DispatcherQueueTimer? _syntheticFloodTimer;

    /// <summary>
    /// Starts a developer-only synthetic load source: <c>--flood=&lt;lines-per-second&gt;</c> on the command
    /// line. Not a product feature, not reachable from the UI, and inert unless the switch is passed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because output-pressure degradation and the frame-aligned flush can otherwise only be
    /// exercised by real hardware flooding a real port. That made them the two hardest things in this app
    /// to re-verify after a change, and therefore the two most likely to rot quietly. With this switch the
    /// degraded path can be entered on demand, with no serial port involved.
    /// </para>
    /// <para>
    /// The generated lines go through the <b>real</b> pipeline: the same queue, the same pause gate, the
    /// same <see cref="MaxQueuedLogEntries"/> backpressure, the same pressure sampling. A generator that
    /// wrote into the display collections directly would prove nothing about the path it is meant to be
    /// stressing, which is the only reason this lives here rather than in a test harness.
    /// </para>
    /// <para>Idempotent, so the switch cannot stack two generators onto one view.</para>
    /// </remarks>
    public void StartSyntheticFlood(int linesPerSecond)
    {
        if (_syntheticFloodTimer is not null)
        {
            return;
        }

        // A port must already be open, and that is an architectural precondition rather than a limit of
        // this generator: FlushPendingLogBatches discards any batch whose port is not in _portsByName
        // ("Ignoring queued data for closed port"), so with nothing open the flood would generate
        // thousands of entries a second and display none of them. That is the one outcome worth
        // refusing outright — a switch that appears to work and does nothing is worse than no switch.
        if (OpenPorts.Count == 0)
        {
            _logger.LogWarning(
                "SYNTHETIC LOAD REFUSED: no open port. The display path discards batches for ports that are not open.");

            _notifications.Notify(
                "合成负载需要先打开至少一个串口（显示路径会丢弃未打开端口的数据）",
                InfoBarSeverity.Error);

            return;
        }

        var rate = Math.Clamp(linesPerSecond, MinFloodLinesPerSecond, MaxFloodLinesPerSecond);
        var linesPerTick = Math.Max(1, rate / FloodTicksPerSecond);

        _syntheticFloodTimer = _dispatcherQueue.CreateTimer();
        _syntheticFloodTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / FloodTicksPerSecond);
        _syntheticFloodTimer.IsRepeating = true;
        _syntheticFloodTimer.Tick += (_, _) => PumpSyntheticFlood(linesPerTick);
        _syntheticFloodTimer.Start();

        // Loud on purpose: a build running at 20k lines/s must be impossible to mistake for a real one,
        // both in the log and on screen.
        _logger.LogWarning(
            "SYNTHETIC LOAD ENABLED: {Rate} lines/s. Developer switch, not a feature. Port={Port}",
            rate,
            OpenPorts[0].PortName);

        _notifications.Notify($"合成负载已开启（{rate} 行/s）— 开发者开关", InfoBarSeverity.Warning);
    }

    /// <summary>Queues one tick's worth of generated lines.</summary>
    private void PumpSyntheticFlood(int linesPerTick)
    {
        // The port can be closed while the generator is running. Bail out rather than produce a tick of
        // entries the flush is guaranteed to discard.
        if (OpenPorts.Count == 0)
        {
            return;
        }

        // Rows claim the same port they are coloured by, so the per-port statistics branch is exercised
        // too — that is part of what makes this a load source rather than just a log generator.
        var portName = OpenPorts[0].PortName;
        var colorHex = PortColorPalette.Resolve(PortColorPalette.DefaultRxHex, _appliedIsDark);

        var logs = new List<LogEntry>(linesPerTick);
        var now = DateTime.Now;

        for (var i = 0; i < linesPerTick; i++)
        {
            logs.Add(new LogEntry
            {
                // One timestamp for the whole tick: a flood is what a chunk of bytes arriving at once
                // actually looks like, and it keeps the per-line cost down to what is being measured.
                Timestamp = now,
                PortName = portName,
                Content = BuildSyntheticLine(i),
                Kind = LogEntryKind.Received,
                ColorHex = colorHex,
            });
        }

        // Sampled the way the receive path samples a chunk, so the pressure service sees the same shape of
        // input a real flood produces.
        _outputPressure.NoteIncoming(linesPerTick * SyntheticLoadBytesPerLine, linesPerTick, 0);

        EnqueueSyntheticBatch(portName, logs);
    }

    /// <summary>Content of one generated line.</summary>
    /// <remarks>
    /// Varied in length and wording, and it includes a token a highlight rule will match on purpose: a
    /// flood of identical one-word lines would not exercise the per-line work (formatting, filtering,
    /// matching) that real traffic does, which is most of what this is for.
    /// </remarks>
    private static string BuildSyntheticLine(int index) => (index % 7) switch
    {
        0 => $"SIM {index:D6} ERROR sensor timeout on channel {index % 3}",
        1 => $"SIM {index:D6} ok rssi=-{40 + (index % 50)}dBm",
        2 => $"SIM {index:D6} WARN buffer {index % 100}% full, draining",
        3 => $"SIM {index:D6} payload {new string('x', 40 + (index % 60))}",
        4 => $"SIM {index:D6} rx {index % 256} bytes crc=OK",
        5 => $"SIM {index:D6} STATE {index % 5} elapsed {index % 1000}ms",
        _ => $"SIM {index:D6} keepalive",
    };

    /// <summary>
    /// Hands one generated batch to the same queue the receive path uses.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY A MIRROR of the enqueue in <c>OnDataReceived</c> — the pause gate, the
    /// <see cref="MaxQueuedLogEntries"/> backpressure with its rollback, and the flush schedule — rather
    /// than a refactor both call. The receive path is the hot path for every byte this app reads and sits
    /// on the do-not-regress list; reshaping it so a developer-only generator can share fifteen lines would
    /// trade real regression risk for a cosmetic win. The cost of this choice is that the two must be
    /// changed together, and that is the smaller cost.
    /// <para>
    /// A drop is silent here, unlike the receive path, which logs a warning: this ticks 20 times a second
    /// and would otherwise drown the log in its own backpressure. The dropped counter is the signal.
    /// </para>
    /// </remarks>
    private void EnqueueSyntheticBatch(string portName, List<LogEntry> logs)
    {
        // Mirrored, not incidental: the real path drops UI entries while paused (the file log already has
        // them), and "pause during a flood" is one of the behaviours this switch exists to let people test.
        if (IsPaused)
        {
            return;
        }

        var queuedLogCount = Interlocked.Add(ref _queuedLogCount, logs.Count);
        if (queuedLogCount > MaxQueuedLogEntries)
        {
            Interlocked.Add(ref _queuedLogCount, -logs.Count);
            Interlocked.Increment(ref _totalDropped);
            return;
        }

        _pendingLogBatches.Enqueue(new PendingLogBatch
        {
            PortName = portName,
            Logs = logs,
        });

        SchedulePendingLogFlush();
    }

    #endregion

    /// <summary>
    /// Queues a locally generated entry — TX, tuning summary, or a connection event — for the next UI flush.
    /// </summary>
    /// <remarks>
    /// It goes through the same <see cref="_pendingLogBatches"/> queue as received data rather than
    /// writing into <see cref="AllLogs"/> / <see cref="DisplayLogs"/> directly. Doing it directly
    /// bypassed every safety net the receive path has: the FIFO trim (so a paused or
    /// high-frequency-send session grew the collections without bound), the search filter (so sent
    /// lines appeared in a filtered view they did not match), the per-flush batch window and the
    /// queued-count cap — and it fired an individual CollectionChanged per entry.
    /// </remarks>
    private void AddLocalLog(string portName, LogEntry logEntry)
    {
        var queuedLogCount = Interlocked.Add(ref _queuedLogCount, 1);
        if (queuedLogCount > MaxQueuedLogEntries)
        {
            Interlocked.Add(ref _queuedLogCount, -1);
            Interlocked.Increment(ref _totalDropped);
            _logger.LogWarning("Dropping locally generated log entry: queued logs exceeded limit. Port={Port}, Limit={Limit}",
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

    /// <summary>
    /// Sends one payload to every target port, encoding it per port through <paramref name="dataForPort"/>.
    /// </summary>
    /// <remarks>
    /// Plain async sends: writes are async IO, so the old LongRunning dedicated threads (one per port,
    /// blocking on GetResult) bought nothing but thread-per-port waste. The payloads are materialised in a
    /// loop rather than through a lazy <c>Select</c>, so the encoder runs exactly once per port and before
    /// anything is awaited — a second evaluation could otherwise read state that has since changed, and
    /// would disagree with the bytes already on the wire.
    /// </remarks>
    private Task<PortSendResult[]> SendDataToPortsAsync(
        IReadOnlyList<string> targetPorts,
        Func<string, byte[]> dataForPort)
    {
        var sends = new List<Task<PortSendResult>>(targetPorts.Count);
        foreach (var portName in targetPorts)
        {
            sends.Add(SendDataToPortWorkerAsync(portName, dataForPort(portName)));
        }

        return Task.WhenAll(sends);
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

    /// <summary>Most sent-log lines one send may produce.</summary>
    /// <remarks>
    /// A multi-line send writes one entry per line <em>per port</em>, so an unbounded paste would flood the
    /// log it is describing. The cap is on the log only — the full payload always goes on the wire, and the
    /// replacement row says how many lines were not listed individually.
    /// </remarks>
    private const int MaxSentLogLinesPerSend = 20;

    private static IReadOnlyList<string> CapSentLogLines(IReadOnlyList<string> lines)
    {
        if (lines.Count <= MaxSentLogLinesPerSend)
        {
            return lines;
        }

        var capped = new List<string>(lines.Take(MaxSentLogLinesPerSend))
        {
            $"…（本行内容与其余 {lines.Count - MaxSentLogLinesPerSend} 行未逐行显示，均已发送）"
        };

        return capped;
    }

    /// <summary>Queues one sent-log entry per port per line.</summary>
    private void AddSentLogs(IReadOnlyList<string> targetPorts, IReadOnlyList<string> displayLines)
    {
        foreach (var portName in targetPorts)
        {
            foreach (var line in displayLines)
            {
                var logEntry = new LogEntry
                {
                    PortName = portName,
                    Content = line,
                    Kind = LogEntryKind.Sent,
                    ColorHex = TxColorHexResolved
                };
                AddLocalLog(portName, logEntry);
            }
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
                Kind = LogEntryKind.Sent,
                ColorHex = TxColorHexResolved
            };
            AddLocalLog(portName, logEntry);
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

            // The pressure verdict described a stream that is no longer on screen. Left armed it would
            // keep the first flushes after the clear degraded for up to OutputPressureService.QuietMs.
            _outputPressure.Reset();

            // Clear both AllLogs and DisplayLogs collections
            AllLogs.Clear();
            DisplayLogs.Clear();

            // Nothing left to scroll back through, so the view is at the newest row again.
            IsLogPinnedToBottom = true;

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

            _openPortConfigs.Remove(portName);
            _autoInitArmed.TryRemove(portName, out _);
            _ = PersistSessionRestoreAsync();

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
    // multi-byte character or a text line can be split across two DataReceived events.
    // Decoding each chunk in isolation turned split characters into U+FFFD (which the garbage
    // detector then flagged) and split lines into two broken entries. A persistent Decoder
    // carries incomplete byte sequences across chunks; a StringBuilder carries the partial
    // tail line until its newline arrives.
    //
    // The encoding arrives as a constructor parameter rather than a mutable field: a Decoder cannot be
    // re-pointed at another encoding, so switching means dropping the whole assembler. That happens on
    // the UI thread (ApplyTextEncodingChangeAsync, and the TryRemove in ClosePortAsync / ClearLogs); the
    // read thread is single-threaded per port and either keeps using the instance it already holds for
    // one more chunk or builds a fresh one — there is no torn state either way.
    private sealed class PortLineAssembler
    {
        public PortLineAssembler(Encoding textEncoding)
        {
            TextEncoding = textEncoding;
            Decoder = textEncoding.GetDecoder();
        }

        /// <summary>The encoding this assembler decodes with; also the source of the char-buffer bound.</summary>
        public readonly Encoding TextEncoding;

        public readonly StringBuilder Pending = new(128);

        // Scratch char buffer sized to the largest chunk seen (GetMaxCharCount upper bound).
        public char[] DecodeBuffer = Array.Empty<char>();

        // Decoder is not thread-safe, but all chunks for one port arrive on the serial
        // driver's single DataReceived worker thread, so access is serialized. The Encoding instance is
        // shared and safe to re-read; the Decoder deliberately is not.
        public readonly Decoder Decoder;
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

            // Decode incrementally through the port's persistent decoder so multi-byte sequences split
            // across chunk boundaries don't become U+FFFD garbage. Every encoding the app offers decodes
            // with a replacement fallback (see SerialEncodings.Resolve), so GetChars can never throw —
            // there is no decode failure path to handle here.
            //
            // The encoding is looked up on this (the read) thread, which is why the service keeps it in a
            // concurrent map rather than in state the UI thread owns.
            var portName = e.PortName;
            var assembler = _lineAssemblers.GetOrAdd(portName, name => new PortLineAssembler(
                SerialEncodings.Resolve(_serialPortService.GetPortTextEncodingName(name))));

            var maxChars = assembler.TextEncoding.GetMaxCharCount(e.Data.Length);
            if (assembler.DecodeBuffer.Length < maxChars)
            {
                assembler.DecodeBuffer = new char[maxChars];
            }

            var charsDecoded = assembler.Decoder.GetChars(
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

            // Report the accepted chunk to the pressure sampler. Runs on this port's read thread and
            // is lock-free and allocation-free: it only counts, it decides nothing about the data.
            // Lines (not bytes) drive the flood verdict because a line is what the UI pays for, and
            // the un-terminated tail doubles as the long-line measure — ExtractCompleteLines leaves
            // whatever has no terminator in Pending, which is exactly the "line that never ends" case.
            _outputPressure.NoteIncoming(dataSize, maxLines, assembler.Pending.Length);

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
                    Kind = LogEntryKind.Received,
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
            _appliedFlushIntervalMs = FlushIntervalMs;
            _flushTimer.IsRepeating = false;
            _flushTimer.Tick += (_, _) => FlushPendingLogBatches();
        }

        if (!_flushTimer.IsRunning)
        {
            // The cadence is re-decided for every armed tick rather than fixed at creation, because a
            // flood is exactly when the flush rate matters most. The assignment sits inside the
            // IsRunning guard: a DispatcherQueueTimer's Interval may only be changed while it is
            // stopped, and the timer is non-repeating so a fired tick leaves it stopped.
            var intervalMs = ResolveFlushIntervalMs();
            if (intervalMs != _appliedFlushIntervalMs)
            {
                _flushTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
                _appliedFlushIntervalMs = intervalMs;
            }

            _flushTimer.Start();
        }
    }

    /// <summary>
    /// Picks the cadence the next flush tick is armed with: <see cref="FlushIntervalMs"/> normally,
    /// <see cref="DegradedFlushIntervalMs"/> while the receive path is under output pressure.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread, once per armed tick. This is the cadence half of the pressure
    /// mechanism — <see cref="IOutputPressureService"/> decides *whether* the flush may do less,
    /// this decides *how often* it runs at all, and
    /// <see cref="MaxUiLogEntriesPerFlushDegraded"/> keeps the drain rate unchanged so the two halves
    /// stay consistent. The interval never drops below <see cref="FlushIntervalMs"/>: the low-rate
    /// case is what this app normally runs in, and a line that appears later than 20 Hz reads as lag
    /// rather than as smoothness.
    /// </remarks>
    private int ResolveFlushIntervalMs() =>
        _outputPressure.Current.IsDegraded ? DegradedFlushIntervalMs : FlushIntervalMs;

    private void FlushPendingLogBatches()
    {
        // Read the verdict once and feed both decisions from the same reading: the batch budget here
        // and the optional-work gate further down. Reading it twice could straddle a window boundary
        // and give this flush a degraded budget with a non-degraded gate.
        var degraded = _outputPressure.Current.IsDegraded;
        var entryBudget = degraded ? MaxUiLogEntriesPerFlushDegraded : MaxUiLogEntriesPerFlush;

        // Drop keyword highlighting for as long as the flood lasts. SetProperty only notifies on a real
        // change, which matters here: this line runs on every flush, and notifying per flush would put
        // a property change plus a viewport walk on the UI thread for a value that had not moved.
        IsHighlightSuppressed = degraded;

        var processedLogCount = 0;
        var batchesToFlush = new List<PendingLogBatch>();

        while (processedLogCount < entryBudget &&
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
            var searchMatcher = string.IsNullOrEmpty(searchTextSnapshot)
                ? null
                : GetOrCreateSearchMatcher(searchTextSnapshot, IsRegexSearch, IsCaseSensitiveSearch);

            // processedLogCount already holds the exact number of dequeued entries: the previous
            // batchesToFlush.Sum(batch => batch.Logs.Count) recomputed it with a LINQ delegate
            // allocation on every single flush.
            var allLogsToAdd = new List<LogEntry>(processedLogCount);
            var displayLogsToAdd = new List<LogEntry>(processedLogCount);

            foreach (var pendingBatch in batchesToFlush)
            {
                if (!openPortMap.TryGetValue(pendingBatch.PortName, out var portVm))
                {
                    _logger.LogDebug("Ignoring queued data for closed port: {Port}", pendingBatch.PortName);
                    continue;
                }

                allLogsToAdd.AddRange(pendingBatch.Logs);
                _pendingStatsPorts.Add(pendingBatch.PortName);

                if (searchMatcher == null)
                {
                    displayLogsToAdd.AddRange(pendingBatch.Logs);
                }
                else if (searchMatcher.IsValid)
                {
                    // The port name is constant for the whole batch — match it once instead of
                    // running the matcher over it for every entry.
                    var portNameMatches = searchMatcher.IsMatch(pendingBatch.PortName);
                    foreach (var logEntry in pendingBatch.Logs)
                    {
                        if (portNameMatches || searchMatcher.IsMatch(logEntry.Content))
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

            MatchCount = searchMatcher is { IsValid: true } ? DisplayLogs.Count : 0;

            // Refresh port stats at most every StatsRefreshIntervalMs; the exception is the
            // final flush of a stream (queue drained), where we refresh so the counters
            // settle on their exact final values.
            //
            // This block is the flush's optional work, and it is the first thing to go under output
            // pressure: it costs one GetStatistics call per touched port plus a PropertyChanged per
            // bound counter, for numbers the next flush is about to overwrite anyway. While degraded
            // only the stream-settled refresh runs — that is the one that leaves the counters on their
            // exact final values — and because _lastStatsRefreshTick is deliberately not advanced
            // while skipping, the normal refresh happens again on the first flush after the flood
            // clears. Nothing here can lose data: _pendingStatsPorts only accumulates port names.
            var streamSettled = _pendingLogBatches.IsEmpty;
            var nowTick = Environment.TickCount64;
            if (_pendingStatsPorts.Count > 0 &&
                (streamSettled ||
                 (nowTick - _lastStatsRefreshTick >= StatsRefreshIntervalMs && !degraded)))
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
    /// Returns the cached matcher for the given query, rebuilding it only when the query, the mode or
    /// the case-sensitivity flag changes. Called from the 50ms flush loop and from FilterLogs — both
    /// on the UI thread, which is why recompiling per call (milliseconds with Compiled) was a direct
    /// hit on UI responsiveness while a search is active.
    /// </summary>
    private SearchMatcher GetOrCreateSearchMatcher(string query, bool useRegex, bool caseSensitive)
    {
        var cacheKey = $"{useRegex}|{caseSensitive}|{query}";

        if (_cachedSearchMatcher != null &&
            string.Equals(_cachedSearchMatcherKey, cacheKey, StringComparison.Ordinal))
        {
            return _cachedSearchMatcher;
        }

        _cachedSearchMatcher = SearchMatcher.Create(query, useRegex, caseSensitive, _logger);
        _cachedSearchMatcherKey = cacheKey;
        return _cachedSearchMatcher;
    }

    /// <summary>
    /// One predicate for both search modes. <see cref="FilterLogs"/> and
    /// <see cref="FlushPendingLogBatches"/> share it so the already-displayed set and the
    /// newly-arriving set can never disagree on how a query is interpreted.
    /// </summary>
    private sealed class SearchMatcher
    {
        private readonly ILogger _logger;
        private readonly string _query;
        private readonly bool _useRegex;
        private readonly StringComparison _comparison;
        private readonly Regex? _regex;

        private SearchMatcher(
            string query,
            bool useRegex,
            bool caseSensitive,
            Regex? regex,
            ILogger logger,
            bool isValid)
        {
            _query = query;
            _useRegex = useRegex;
            _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            _regex = regex;
            _logger = logger;
            IsValid = isValid;
        }

        /// <summary>False only for a regex that failed to parse; text mode is always valid.</summary>
        public bool IsValid { get; }

        public static SearchMatcher Create(string? query, bool useRegex, bool caseSensitive, ILogger logger)
        {
            query ??= string.Empty;

            // An empty query is not a filter at all — callers check that separately and never ask
            // this instance to match. Text mode needs no compilation whatsoever.
            if (query.Length == 0 || !useRegex)
            {
                return new SearchMatcher(query, useRegex, caseSensitive, null, logger, isValid: true);
            }

            try
            {
                var options = RegexOptions.Compiled;
                if (!caseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                var regex = new Regex(query, options, TimeSpan.FromMilliseconds(100));
                return new SearchMatcher(query, useRegex, caseSensitive, regex, logger, isValid: true);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", query);
                return new SearchMatcher(query, useRegex, caseSensitive, null, logger, isValid: false);
            }
        }

        /// <summary>
        /// Literal <c>Contains</c> in text mode, regex match in regex mode. A match timeout degrades
        /// to "no match" instead of throwing into the 50ms flush loop.
        /// </summary>
        public bool IsMatch(string? text)
        {
            if (text == null)
            {
                return false;
            }

            if (!_useRegex)
            {
                return _query.Length == 0 || text.Contains(_query, _comparison);
            }

            if (_regex == null)
            {
                return false;
            }

            try
            {
                return _regex.IsMatch(text);
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "Regex match timeout for pattern: {Pattern}", _query);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Regex match failed for pattern: {Pattern}", _query);
                return false;
            }
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
        // Scrolled away from the newest row: do not pull rows out from under the reader. The trim
        // publishes a Reset (every realized container discarded) and the rows that survive move up
        // by the number removed, so the sentence being read slides off the screen. The hard cap
        // below is the escape hatch — past it, trimming is the lesser evil.
        if (!IsLogPinnedToBottom && DisplayLogs.Count <= DisplayLogHardCap)
        {
            return;
        }

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
        var timestamp = e.Timestamp;
        var content = LogEventText.DescribeStateChange(newState);

        // The file half is written here, on the raising thread, rather than inside the dispatcher callback.
        // The close path raises Disconnected from inside SerialPortService.ClosePortAsync and stops that
        // port's file logger a few lines later, so an enqueued write can lose the one event a post-mortem
        // needs most. WriteLogs only queues (the writer flushes on its own timer) and is thread-safe.
        WriteEventToFile(portName, timestamp, content);

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                StatusMessage = $"Port {portName}: {newState}";
                _logger.LogInformation("Port {PortName} state changed: {OldState} -> {NewState}",
                    portName, oldState, newState);

                // Collapsed error counts first, so a port that dies in the middle of a storm still records
                // how bad it was immediately before the end.
                DrainEventAggregates();

                // Auto-init is queued from inside the sequence below rather than awaited: the row rendered
                // here is "connected", and the send must not be able to appear ahead of it in the log.
                MaybeRunAutoInitSequence(portName, newState);

                // No aggregation for a state change: it is inherently low-frequency, and a reconnect storm is
                // exactly what the reader is looking for rather than noise to be collapsed.
                QueueEventRow(portName, timestamp, content);
            }
            catch (Exception ex)
            {
                // Fallback logging if UI update fails
                _logger.LogError(ex, "Failed to update UI with state change");
            }
        });
    }

    /// <summary>
    /// 把一次连接事件写成日志行；同一个键在窗口内重复时改为计数汇总。
    /// </summary>
    /// <param name="portName">事件所属端口。</param>
    /// <param name="timestamp">
    /// 事件发生的时刻，不是这次提交的时刻：事件行经 <c>_pendingLogBatches</c> 会晚几十毫秒出现，用提交
    /// 时刻会和真实因果错位。
    /// </param>
    /// <param name="content">行内容。</param>
    /// <param name="aggregateKey">
    /// 非 null 时参与重复折叠。错误必须传——帧错误/溢出风暴可达 30+/s；状态变化传 null，因为重连风暴本身
    /// 就是读者要找的信息。
    /// </param>
    private void ReportPortEvent(string portName, DateTime timestamp, string content, string? aggregateKey)
    {
        if (aggregateKey is not null)
        {
            if (!_eventAggregates.Record(aggregateKey, portName, content))
            {
                // A repeat inside the window: the line is already in the log, and the count surfaces when the
                // window drains.
                return;
            }

            StartEventAggregateTimer();
        }

        WriteEventToFile(portName, timestamp, content);
        QueueEventRow(portName, timestamp, content);
    }

    /// <summary>
    /// 把一行事件写进该端口的文件日志。线程安全且不阻塞（<c>WriteLogs</c> 只入队）。
    /// </summary>
    private void WriteEventToFile(string portName, DateTime timestamp, string content)
        => _fileLoggerService.WriteLogs(portName, new[] { CreateEventEntry(portName, timestamp, content) });

    /// <summary>把一行事件交给界面日志的下一步 flush。</summary>
    private void QueueEventRow(string portName, DateTime timestamp, string content)
        => AddLocalLog(portName, CreateEventEntry(portName, timestamp, content));

    /// <summary>
    /// 构造一条事件行。
    /// </summary>
    /// <remarks>
    /// The file copy and the row are deliberately separate objects rather than one shared instance: the file
    /// write for a state change happens on the raising thread while the row has to be created on the UI
    /// thread, and one mutable <c>LogEntry</c> crossing threads for the sake of saving an allocation is not a
    /// trade worth making.
    /// </remarks>
    private LogEntry CreateEventEntry(string portName, DateTime timestamp, string content) => new()
    {
        Timestamp = timestamp,
        PortName = portName,
        Content = content,
        Kind = LogEntryKind.Event,
        ColorHex = LogRowColorPalette.EventRow(_isDarkTheme),
    };

    /// <summary>
    /// 启动（或重启）事件行的重复计数窗口。
    /// </summary>
    /// <remarks>
    /// Non-repeating, so a fired tick leaves it stopped and the next occurrence opens a fresh window — which
    /// is what makes a continuous storm produce one summary per second rather than one per error.
    /// </remarks>
    private void StartEventAggregateTimer()
    {
        if (_eventAggregateTimer is null)
        {
            _eventAggregateTimer = _dispatcherQueue.CreateTimer();
            _eventAggregateTimer.Interval = TimeSpan.FromMilliseconds(EventRepeatWindowMs);
            _eventAggregateTimer.IsRepeating = false;
            _eventAggregateTimer.Tick += (_, _) => DrainEventAggregates();
        }

        if (!_eventAggregateTimer.IsRunning)
        {
            _eventAggregateTimer.Start();
        }
    }

    /// <summary>
    /// 窗口结束时为每个被折叠的事件行补一条计数汇总。
    /// </summary>
    /// <remarks>
    /// The summary is a second line rather than an edit of the first. <c>LogEntry.FormattedText</c> is bound
    /// <c>OneTime</c> — that is part of what keeps the two-element row cheap — so rewriting an entry that is
    /// already on screen would leave the rendered row showing the old text until its container was recycled.
    /// Two lines per storm is the price of never displaying a stale count.
    /// </remarks>
    private void DrainEventAggregates()
    {
        if (!_eventAggregates.HasPending)
        {
            return;
        }

        var windowSeconds = EventRepeatWindowMs / 1000;

        foreach (var (portName, line, count) in _eventAggregates.DrainRepeats())
        {
            var content = LogEventText.WithRepeatCount(line, count, windowSeconds);
            var now = DateTime.Now;

            WriteEventToFile(portName, now, content);
            QueueEventRow(portName, now, content);
        }
    }

    private void OnErrorOccurred(object? sender, Services.ErrorEventArgs e)
    {
        // Capture values before dispatching to avoid closure issues
        var portName = e.PortName;
        var errorMessage = e.ErrorMessage;
        var exception = e.Exception;
        var timestamp = e.Timestamp;

        // Always log on background thread — logging never blocks UI.
        _logger.LogError(exception, "Error on port {PortName}", portName);

        // Throttle StatusMessage updates. Frame/Overrun error storms can fire 30+/sec
        // (wrong baud rate, line noise). Every update marshals to the UI thread and
        // re-renders the status bar, contributing to the wheel/selection lag the user
        // experiences. Keep the most recent error visible, drop the rest.
        //
        // The decision is computed here but applied inside the dispatcher callback, and it deliberately does
        // NOT gate the log row: ReportPortEvent has its own aggregation, which collapses repeats into a count
        // instead of dropping them. Gating both behind one 250 ms throttle is the obvious-looking shape and
        // the wrong one — it would silently discard 29 of every 30 errors from the log.
        var nowTicks = DateTime.UtcNow.Ticks;
        var lastTicks = Interlocked.Read(ref _lastErrorStatusTicks);
        var elapsedMs = (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;
        var updateStatus = elapsedMs >= ErrorStatusThrottleMs;
        if (updateStatus)
        {
            Interlocked.Exchange(ref _lastErrorStatusTicks, nowTicks);
        }

        var content = LogEventText.DescribeError(errorMessage);
        var aggregateKey = LogEventText.AggregateKey(portName, errorMessage);

        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (updateStatus)
                {
                    StatusMessage = $"Error on {portName}: {errorMessage}";
                }

                ReportPortEvent(portName, timestamp, content, aggregateKey);
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
            Parity = Parity,
            Handshake = Handshake,
            TextEncodingName = await ResolvePortEncodingForOpenAsync(portName)
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
/// One entry of a serial line-parameter picker (data bits are a plain int list; stop bits, parity and
/// flow control use this so the picker can show a Chinese label).
/// </summary>
/// <remarks>
/// A display wrapper rather than a bare enum value, for the same reason <see cref="PortColorOption"/>
/// exists: <c>StopBits.OnePointFive</c> is not something a user reads off a combo box, and the enum name
/// is what a bare binding would render. The enum stays the value that is persisted and applied — only
/// the label lives here.
/// </remarks>
public sealed class SerialParameterOption<T> where T : struct
{
    public SerialParameterOption(T value, string name)
    {
        Value = value;
        Name = name;
    }

    /// <summary>The enum value. This is what <c>SelectedValuePath</c> binds to.</summary>
    public T Value { get; }

    /// <summary>Label shown in the picker.</summary>
    public string Name { get; }

    /// <summary>
    /// The label, so a picker that forgets its <c>ItemTemplate</c> shows the name instead of the type name.
    /// </summary>
    /// <remarks>
    /// Not theoretical: a <c>ComboBox</c> with neither an <c>ItemTemplate</c> nor a
    /// <c>DisplayMemberPath</c> falls back to <see cref="object.ToString"/>, which for this type renders
    /// <c>SerialPortTool.ViewModels.SerialParameterOption`1[...]</c> in the closed box. A type whose whole
    /// purpose is to carry a label should not be able to fail that way.
    /// </remarks>
    public override string ToString() => Name;
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
