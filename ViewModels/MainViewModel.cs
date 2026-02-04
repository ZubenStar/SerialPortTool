using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.ViewModels;

/// <summary>
/// ObservableCollection that supports incremental batch operations without full resets
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification = false;

    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) return;

        var itemsList = items.ToList();
        if (itemsList.Count == 0) return;

        _suppressNotification = true;
        foreach (var item in itemsList)
        {
            Items.Add(item);
        }
        _suppressNotification = false;

        // Use Add action with multiple items instead of Reset for smoother UI updates
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            itemsList,
            Items.Count - itemsList.Count));
    }

    public void RemoveRange(IEnumerable<T> items)
    {
        if (items == null) return;

        var itemsList = items.ToList();
        if (itemsList.Count == 0) return;

        _suppressNotification = true;
        foreach (var item in itemsList)
        {
            Items.Remove(item);
        }
        _suppressNotification = false;

        // Notify with Reset for removals (less common operation)
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
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
public partial class MainViewModel : ObservableObject
{
/// <summary>
/// 波特率检测建议事件
/// </summary>
public event EventHandler<BaudRateSuggestionEventArgs>? BaudRateSuggested;
    private readonly ISerialPortService _serialPortService;
    private readonly ILogFilterService _logFilterService;
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

    [ObservableProperty]
    private RangeObservableCollection<LogEntry> _allLogs = new();

    [ObservableProperty]
    private RangeObservableCollection<LogEntry> _displayLogs = new();

    [ObservableProperty]
    private ObservableCollection<FilterRule> _filters = new();
    
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
                _filterDebounceTimer?.Dispose();
                _filterDebounceTimer = new System.Threading.Timer(_ =>
                {
                    _dispatcherQueue.TryEnqueue(() => FilterLogs());
                }, null, 150, System.Threading.Timeout.Infinite);
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

    [ObservableProperty]
    private bool _isRegexValid = true;

    [ObservableProperty]
    private string _regexErrorMessage = string.Empty;

    [ObservableProperty]
    private int _matchCount = 0;

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
            _ = new Regex(SearchText, RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private bool _sendAsHex = false;

    private const int MaxDisplayLogs = 2000; // Increased limit with optimizations
    private const int BatchProcessSize = 50; // Process logs in batches

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isScanning = false;

    [ObservableProperty]
    private string _sendText = string.Empty;

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
        1152000, 3000000, 6000000
    };

    [ObservableProperty]
    private string _customBaudRate = string.Empty;

    [ObservableProperty]
    private bool _useCustomBaudRate = false;

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
        ILogFilterService logFilterService,
        IFileLoggerService fileLoggerService,
        ISettingsService settingsService,
        ILogger<MainViewModel> logger,
        Services.IBaudRateDetectorService? baudRateDetectorService = null,
        Services.IDataValidationService? dataValidationService = null)
    {
        _serialPortService = serialPortService;
        _logFilterService = logFilterService;
        _fileLoggerService = fileLoggerService;
        _settingsService = settingsService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _baudRateDetectorService = baudRateDetectorService;
        _dataValidationService = dataValidationService;

        // Subscribe to events
        _serialPortService.DataReceived += OnDataReceived;
        _serialPortService.PortStateChanged += OnPortStateChanged;
        _serialPortService.ErrorOccurred += OnErrorOccurred;
        
        // Subscribe to baud rate detection requests
        if (_serialPortService is SerialPortService serialPortServiceInstance)
        {
            serialPortServiceInstance.BaudRateDetectionRequested += OnBaudRateDetectionRequested;
        }

        // Initialize
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Load saved baud rate settings
        BaudRate = await _settingsService.LoadSettingAsync("BaudRate", 3000000);
        UseCustomBaudRate = await _settingsService.LoadSettingAsync("UseCustomBaudRate", 0) == 1;
        CustomBaudRate = await _settingsService.LoadSettingAsync("CustomBaudRate", string.Empty);
        _logger.LogInformation("Loaded baud rate settings: BaudRate={BaudRate}, UseCustom={UseCustom}, CustomValue={CustomValue}",
            BaudRate, UseCustomBaudRate, CustomBaudRate);

        // Load recent search texts
        await LoadRecentSearchesAsync();

        // Scan ports
        await ScanPortsAsync();
    }

    [RelayCommand]
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
        }
    }

    [RelayCommand]
    private async Task OpenPortAsync(string portName)
    {
        if (string.IsNullOrEmpty(portName))
        {
            StatusMessage = "Please select a port";
            return;
        }

        if (OpenPorts.Any(p => p.PortName == portName))
        {
            StatusMessage = $"Port {portName} is already open";
            return;
        }

        try
        {
            // Determine which baud rate to use
            int baudRateToUse = BaudRate;
            if (UseCustomBaudRate && !string.IsNullOrWhiteSpace(CustomBaudRate))
            {
                if (int.TryParse(CustomBaudRate, out int customRate) && customRate > 0)
                {
                    baudRateToUse = customRate;
                    _logger.LogInformation("Using custom baud rate: {BaudRate}", baudRateToUse);
                }
                else
                {
                    StatusMessage = "Invalid custom baud rate";
                    return;
                }
            }
            else
            {
                _logger.LogInformation("Using standard baud rate: {BaudRate}", baudRateToUse);
            }

            var config = new SerialPortConfig
            {
                PortName = portName,
                BaudRate = baudRateToUse,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity
            };

            var opened = await _serialPortService.OpenPortAsync(config);
            if (opened)
            {
                // Start file logging
                await _fileLoggerService.StartLoggingAsync(portName);

                var portViewModel = new PortViewModel(portName, _serialPortService, _logFilterService, _dispatcherQueue);

                // Initialize statistics display
                var stats = _serialPortService.GetStatistics(portName);
                portViewModel.UpdateStatistics(stats);

                OpenPorts.Add(portViewModel);

                // Save baud rate settings for next time
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
    }

    [RelayCommand]
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
            int baudRateToUse = BaudRate;
            if (UseCustomBaudRate && !string.IsNullOrWhiteSpace(CustomBaudRate))
            {
                if (int.TryParse(CustomBaudRate, out int customRate) && customRate > 0)
                {
                    baudRateToUse = customRate;
                    _logger.LogInformation("OpenAllPorts: Using custom baud rate: {BaudRate}", baudRateToUse);
                }
                else
                {
                    StatusMessage = "Invalid custom baud rate";
                    return;
                }
            }
            else
            {
                _logger.LogInformation("OpenAllPorts: Using standard baud rate: {BaudRate}", baudRateToUse);
            }

            var defaultConfig = new SerialPortConfig
            {
                BaudRate = baudRateToUse,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity
            };

            var openedCount = await _serialPortService.OpenAllPortsAsync(defaultConfig);

            // Start file logging and create ViewModels for each opened port
            foreach (var portName in _serialPortService.GetOpenPorts())
            {
                if (!OpenPorts.Any(p => p.PortName == portName))
                {
                    await _fileLoggerService.StartLoggingAsync(portName);

                    var portViewModel = new PortViewModel(portName, _serialPortService, _logFilterService, _dispatcherQueue);
                    var stats = _serialPortService.GetStatistics(portName);
                    portViewModel.UpdateStatistics(stats);
                    OpenPorts.Add(portViewModel);
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

    [RelayCommand]
    private async Task CloseAllPortsAsync()
    {
        if (OpenPorts.Count == 0)
        {
            StatusMessage = "No open ports to close";
            return;
        }

        try
        {
            var portCount = OpenPorts.Count;
            StatusMessage = $"Closing {portCount} port(s)...";

            // Stop file logging for all ports
            foreach (var port in OpenPorts.ToList())
            {
                await _fileLoggerService.StopLoggingAsync(port.PortName);
            }

            // Close all ports with enhanced cleanup
            await _serialPortService.CloseAllPortsAsync();

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

    public void FilterLogs()
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
                var regex = new Regex(SearchText, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                filtered = AllLogs.Where(log =>
                    regex.IsMatch(log.Content) ||
                    regex.IsMatch(log.PortName))
                    .ToList();
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
        
        // Remove items that no longer match filter (in reverse to maintain indices)
        for (int i = DisplayLogs.Count - 1; i >= 0; i--)
        {
            if (!filteredSet.Contains(DisplayLogs[i]))
            {
                DisplayLogs.RemoveAt(i);
            }
        }
        
        // Add new items that match filter but aren't in DisplayLogs yet
        var displaySet = new HashSet<LogEntry>(DisplayLogs);
        foreach (var log in filtered)
        {
            if (!displaySet.Contains(log))
            {
                DisplayLogs.Add(log);
            }
        }
    }

    public async Task SendDataAsync(string portName, byte[] data)
    {
        await _serialPortService.SendDataAsync(portName, data);
    }

    public async Task SendTextAsync(string portName, string text)
    {
        await _serialPortService.SendTextAsync(portName, text, Encoding.UTF8);
    }

    public void AddSentLog(LogEntry logEntry)
    {
        AllLogs.Add(logEntry);
        DisplayLogs.Add(logEntry);
    }

    [RelayCommand]
    private void ClearLogs()
    {
        try
        {
            _logger.LogInformation("Clearing all logs");
            
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
            
            // Stop file logging
            await _fileLoggerService.StopLoggingAsync(portName);
            
            // Close the port with enhanced cleanup
            await _serialPortService.ClosePortAsync(portName);
            
            var portVm = OpenPorts.FirstOrDefault(p => p.PortName == portName);
            if (portVm != null)
            {
                OpenPorts.Remove(portVm);
            }
            
            StatusMessage = $"✅ Port {portName} closed successfully. Wait 1-2 seconds before reopening.";
            _logger.LogInformation("Port {PortName} closed successfully", portName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error closing port {portName}: {ex.Message}. If port won't reopen, restart the application.";
            _logger.LogError(ex, "Error closing port {PortName}", portName);
        }
    }

    private int _pendingUpdates = 0;
    private const int MaxPendingUpdates = 50; // Rate limit UI updates
    private long _totalDataReceived = 0;
    private long _totalDropped = 0;
    
    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        var dataSize = e.Data?.Length ?? 0;
        Interlocked.Increment(ref _totalDataReceived);
        
        _logger.LogTrace("OnDataReceived called: Port={Port}, Size={Size}bytes, Pending={Pending}",
            e.PortName, dataSize, _pendingUpdates);
        
        // Enhanced rate limiting: Skip if too many pending updates
        if (Interlocked.CompareExchange(ref _pendingUpdates, 0, 0) > MaxPendingUpdates)
        {
            Interlocked.Increment(ref _totalDropped);
            _logger.LogWarning("⚠️ Dropping data update due to high pending count: Pending={Pending}, Port={Port}, TotalReceived={Total}, TotalDropped={Dropped}",
                _pendingUpdates, e.PortName, _totalDataReceived, _totalDropped);
            return;
        }

        // Additional protection: Skip if data size is too large (potential garbage data)
        if (dataSize > 16384) // 16KB limit
        {
            Interlocked.Increment(ref _totalDropped);
            _logger.LogWarning("⚠️ Dropping oversized data packet: Size={Size}bytes, Port={Port}, Limit=16384",
                dataSize, e.PortName);
            return;
        }

        Interlocked.Increment(ref _pendingUpdates);
        _logger.LogTrace("Processing data: Pending now {Pending}", _pendingUpdates);

        try
        {
            // Capture data on background thread
            if (e.Data == null || e.Data.Length == 0)
            {
                Interlocked.Decrement(ref _pendingUpdates);
                _logger.LogTrace("Received null or empty data, skipping: Port={Port}", e.PortName);
                return;
            }
            
            // Safe text decoding with fallback
            string text;
            try
            {
                text = Encoding.UTF8.GetString(e.Data);
            }
            catch
            {
                try
                {
                    text = Encoding.ASCII.GetString(e.Data);
                    _logger.LogDebug("UTF-8 decoding failed for {Port}, using ASCII fallback", e.PortName);
                }
                catch
                {
                    // If both decodings fail, create a safe representation
                    text = $"[Binary data: {dataSize} bytes]";
                    _logger.LogWarning("Both UTF-8 and ASCII decoding failed for {Port}, using binary representation", e.PortName);
                }
            }
            
            var portName = e.PortName;
            
            _logger.LogTrace("Decoded text: Length={Length} chars, Port={Port}", text.Length, portName);
            
            // Build log entries on background thread with optimized string operations
            var newLogs = new List<LogEntry>();
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            
            _logger.LogTrace("Split into {LineCount} lines, Port={Port}", lines.Length, portName);
            
            // Enhanced protection: Limit lines to prevent memory overflow and UI freezing
            var maxLines = Math.Min(lines.Length, 500); // Reduced from 1000 to 500 for better performance
            
            if (lines.Length > maxLines)
            {
                _logger.LogWarning("⚠️ Line count exceeds limit: Got {Count} lines, capping at {Max}, Port={Port}",
                    lines.Length, maxLines, portName);
            }
            
            // Pre-allocate to reduce reallocations
            if (maxLines > 1)
            {
                newLogs.Capacity = maxLines;
            }
            
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
                    line = line.Substring(0, 1000) + "...[truncated]";
                    _logger.LogDebug("Truncated long line: Port={Port}, OriginalLength={Original}", portName, line.Length);
                }
                
                var logEntry = new LogEntry
                {
                    PortName = portName,
                    Content = line,
                    Timestamp = now,
                    IsReceived = true
                };
                
                newLogs.Add(logEntry);
                
                // Write to log file asynchronously (batched internally)
                _ = _fileLoggerService.WriteLogAsync(portName, logEntry);
            }
            
            // Log statistics about data quality
            if (garbageLineCount > 0)
            {
                _logger.LogInformation("Data quality stats for {Port}: Valid={Valid}, Garbage={Garbage}, Total={Total}",
                    portName, validLineCount, garbageLineCount, newLogs.Count);
            }

            if (newLogs.Count == 0)
            {
                _logger.LogTrace("No logs generated after processing, Port={Port}", portName);
                Interlocked.Decrement(ref _pendingUpdates);
                return;
            }
            
            _logger.LogTrace("Generated {Count} log entries, Port={Port}", newLogs.Count, portName);

            // Cached regex for better performance
            Regex? filterRegex = null;
            if (!string.IsNullOrEmpty(SearchText) && IsRegexValid)
            {
                try
                {
                    filterRegex = new Regex(SearchText, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));
                    _logger.LogTrace("Created regex filter: Pattern={Pattern}", SearchText);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create regex filter: Pattern={Pattern}", SearchText);
                    filterRegex = null;
                }
            }

            // Dispatch UI updates with batching for smoother performance
            _logger.LogTrace("Enqueueing UI update: LogCount={Count}, Port={Port}", newLogs.Count, portName);
            
            var enqueued = _dispatcherQueue.TryEnqueue(() =>
            {
                var updateStartTime = DateTime.Now;
                _logger.LogTrace("🔄 UI update started: Port={Port}", portName);
                
                try
                {
                    // Check if port is still active in UI to prevent residual logs from closed ports
                    // This ensures that if a port was closed while updates were still in the dispatcher queue,
                    // they won't be added to the global log collections.
                    if (!OpenPorts.Any(p => p.PortName == portName))
                    {
                        _logger.LogDebug("Ignoring data update for closed port: {Port}", portName);
                        return;
                    }

                    // Batch add to AllLogs with error handling
                    try
                    {
                        var beforeCount = AllLogs.Count;
                        
                        if (AllLogs is RangeObservableCollection<LogEntry> rangeAllLogsAdd)
                        {
                            rangeAllLogsAdd.AddRange(newLogs);
                            _logger.LogTrace("Added {Count} logs to AllLogs using AddRange: Before={Before}, After={After}",
                                newLogs.Count, beforeCount, AllLogs.Count);
                        }
                        else
                        {
                            foreach (var log in newLogs)
                            {
                                AllLogs.Add(log);
                            }
                            _logger.LogTrace("Added {Count} logs to AllLogs individually: Before={Before}, After={After}",
                                newLogs.Count, beforeCount, AllLogs.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error adding to AllLogs: Count={Count}, Port={Port}",
                            newLogs.Count, portName);
                    }
                    
                    // Apply regex filter to new logs
                    var logsToDisplay = newLogs;
                    if (filterRegex != null)
                    {
                        try
                        {
                            var filterStartTime = DateTime.Now;
                            logsToDisplay = newLogs.Where(log =>
                            {
                                try
                                {
                                    return filterRegex.IsMatch(log.Content) || filterRegex.IsMatch(log.PortName);
                                }
                                catch (Exception matchEx)
                                {
                                    _logger.LogTrace("Regex match failed for log: {Error}", matchEx.Message);
                                    return false;
                                }
                            }).ToList();
                            
                            var filterDuration = (DateTime.Now - filterStartTime).TotalMilliseconds;
                            _logger.LogTrace("Filtered {Input} logs to {Output} logs in {Duration}ms",
                                newLogs.Count, logsToDisplay.Count, filterDuration);
                            
                            MatchCount = DisplayLogs.Count + logsToDisplay.Count;
                        }
                        catch (RegexMatchTimeoutException ex)
                        {
                            _logger.LogWarning(ex, "⚠️ Regex match timeout during filter: Pattern={Pattern}", SearchText);
                            logsToDisplay = new List<LogEntry>();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Error filtering logs: Pattern={Pattern}", SearchText);
                            logsToDisplay = newLogs; // Fallback to showing all
                        }
                    }
                    
                    // Batch add to DisplayLogs with error handling
                    try
                    {
                        var beforeDisplayCount = DisplayLogs.Count;
                        
                        if (DisplayLogs is RangeObservableCollection<LogEntry> rangeDisplayLogsAdd && logsToDisplay.Count > 5)
                        {
                            rangeDisplayLogsAdd.AddRange(logsToDisplay);
                            _logger.LogTrace("Added {Count} logs to DisplayLogs using AddRange: Before={Before}, After={After}",
                                logsToDisplay.Count, beforeDisplayCount, DisplayLogs.Count);
                        }
                        else
                        {
                            foreach (var log in logsToDisplay)
                            {
                                DisplayLogs.Add(log);
                            }
                            _logger.LogTrace("Added {Count} logs to DisplayLogs individually: Before={Before}, After={After}",
                                logsToDisplay.Count, beforeDisplayCount, DisplayLogs.Count);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error adding to DisplayLogs: Count={Count}, Port={Port}",
                            logsToDisplay.Count, portName);
                    }

                    // Trim logs in batches to reduce UI updates
                    try
                    {
                        var removeCount = DisplayLogs.Count - MaxDisplayLogs;
                        if (removeCount > 0)
                        {
                            _logger.LogTrace("Trimming DisplayLogs: Removing {Count} items, Current={Current}, Max={Max}",
                                removeCount, DisplayLogs.Count, MaxDisplayLogs);
                            
                            if (DisplayLogs is RangeObservableCollection<LogEntry> rangeDisplayLogsRemove)
                            {
                                rangeDisplayLogsRemove.RemoveRange(DisplayLogs.Take(removeCount).ToList());
                                _logger.LogTrace("Trimmed DisplayLogs using RemoveRange: New count={Count}", DisplayLogs.Count);
                            }
                            else
                            {
                                for (int i = 0; i < removeCount && i < DisplayLogs.Count; i++)
                                {
                                    DisplayLogs.RemoveAt(0);
                                }
                                _logger.LogTrace("Trimmed DisplayLogs individually: New count={Count}", DisplayLogs.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error trimming DisplayLogs: RemoveCount={Count}, CurrentCount={Current}",
                            DisplayLogs.Count - MaxDisplayLogs, DisplayLogs.Count);
                    }
                    
                    // Trim AllLogs
                    try
                    {
                        var removeAllCount = AllLogs.Count - MaxDisplayLogs * 2;
                        if (removeAllCount > 0)
                        {
                            _logger.LogTrace("Trimming AllLogs: Removing {Count} items, Current={Current}, Max={Max}",
                                removeAllCount, AllLogs.Count, MaxDisplayLogs * 2);
                            
                            if (AllLogs is RangeObservableCollection<LogEntry> rangeAllLogsRemove)
                            {
                                rangeAllLogsRemove.RemoveRange(AllLogs.Take(removeAllCount).ToList());
                                _logger.LogTrace("Trimmed AllLogs using RemoveRange: New count={Count}", AllLogs.Count);
                            }
                            else
                            {
                                for (int i = 0; i < removeAllCount && i < AllLogs.Count; i++)
                                {
                                    AllLogs.RemoveAt(0);
                                }
                                _logger.LogTrace("Trimmed AllLogs individually: New count={Count}", AllLogs.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error trimming AllLogs: RemoveCount={Count}, CurrentCount={Current}",
                            AllLogs.Count - MaxDisplayLogs * 2, AllLogs.Count);
                    }

                    // Update port-specific logs and statistics
                    try
                    {
                        var portVm = OpenPorts.FirstOrDefault(p => p.PortName == portName);
                        if (portVm != null)
                        {
                            // Batch add logs to port
                            foreach (var logEntry in newLogs)
                            {
                                portVm.AddLog(logEntry);
                            }
                            
                            var stats = _serialPortService.GetStatistics(portName);
                            portVm.UpdateStatistics(stats);
                            
                            _logger.LogTrace("Updated port statistics: Port={Port}, RxBytes={Rx}, TxBytes={Tx}",
                                portName, stats.ReceivedBytes, stats.SentBytes);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Port ViewModel not found: Port={Port}", portName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "❌ Error updating port statistics: Port={Port}", portName);
                    }
                    
                    var updateDuration = (DateTime.Now - updateStartTime).TotalMilliseconds;
                    _logger.LogTrace("✅ UI update completed: Port={Port}, Duration={Duration}ms", portName, updateDuration);
                    
                    if (updateDuration > 100)
                    {
                        _logger.LogWarning("⚠️ Slow UI update detected: Duration={Duration}ms, Port={Port}",
                            updateDuration, portName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Critical error updating UI with received data: Port={Port}", portName);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingUpdates);
                    _logger.LogTrace("UI update finished: Pending now {Pending}", _pendingUpdates);
                }
            });

            if (!enqueued)
            {
                Interlocked.Decrement(ref _pendingUpdates);
                _logger.LogError("❌ Failed to enqueue UI update: Port={Port}, Pending={Pending}",
                    portName, _pendingUpdates);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _pendingUpdates);
            _logger.LogError(ex, "❌ Critical error in OnDataReceived: Port={Port}, Size={Size}bytes",
                e.PortName, e.Data?.Length ?? 0);
        }
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
        
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                StatusMessage = $"Error on {portName}: {errorMessage}";
                _logger.LogError(exception, "Error on port {PortName}", portName);
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
        
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                StatusMessage = $"检测到 {portName} 波特率可能不正确: {reason}";
                _logger.LogWarning("Baud rate detection requested for {PortName}: {Reason}", portName, reason);
                
                if (_baudRateDetectorService != null)
                {
                    StatusMessage = $"正在为 {portName} 检测最佳波特率...";
                    
                    try
                    {
                        var detectionResults = await _baudRateDetectorService.DetectOptimalBaudRateAsync(portName);
                        
                        if (detectionResults.Count > 0 && detectionResults[0].ConfidenceScore > 0.5)
                        {
                            var bestBaudRate = detectionResults[0].BaudRate;
                            StatusMessage = $"建议将 {portName} 波特率设置为 {bestBaudRate} (置信度: {detectionResults[0].ConfidenceScore:F2})";
                            
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
                                StatusMessage = $"自动将 {portName} 波特率从 {currentBaudRate} 切换到 {bestBaudRate}";
                                await SwitchPortBaudRateAsync(portName, bestBaudRate);
                            }
                        }
                        else
                        {
                            StatusMessage = $"无法为 {portName} 确定最佳波特率，请手动检查";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error during baud rate detection for {PortName}", portName);
                        StatusMessage = $"波特率检测失败: {ex.Message}";
                    }
                }
                else
                {
                    StatusMessage = $"波特率检测服务不可用，请手动调整 {portName} 的波特率";
                }
            }
            catch (Exception ex)
            {
                // Fallback logging if UI update fails
                _logger.LogError(ex, "Failed to handle baud rate detection request");
                StatusMessage = $"处理波特率检测请求时出错";
            }
        });
    }

    public async Task SwitchPortBaudRateAsync(string portName, int newBaudRate)
    {
        try
        {
            // 关闭当前端口
            await _serialPortService.ClosePortAsync(portName);
            
            // 等待一段时间确保端口完全释放
            await Task.Delay(500);
            
            // 创建新配置
            var newConfig = new SerialPortConfig
            {
                PortName = portName,
                BaudRate = newBaudRate,
                DataBits = DataBits,
                StopBits = StopBits,
                Parity = Parity
            };
            
            // 重新打开端口
            var opened = await _serialPortService.OpenPortAsync(newConfig);
            
            if (opened)
            {
                // 重置验证状态
                _dataValidationService?.ResetValidationState(portName);
                
                _logger.LogInformation("Successfully switched {PortName} to baud rate {BaudRate}", portName, newBaudRate);
            }
            else
            {
                _logger.LogError("Failed to reopen {PortName} with new baud rate {BaudRate}", portName, newBaudRate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error switching baud rate for {PortName}", portName);
            throw;
        }
    }

    public string GetLogDirectory()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return System.IO.Path.Combine(documentsPath, "SerialPortTool", "Logs");
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
/// 单个串口的视图模型
/// </summary>
public partial class PortViewModel : ObservableObject
{
    private readonly ISerialPortService _serialPortService;
    private readonly ILogFilterService _logFilterService;

    [ObservableProperty]
    private string _portName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<LogEntry> _logs = new();

    [ObservableProperty]
    private ObservableCollection<LogEntry> _filteredLogs = new();

    [ObservableProperty]
    private string _sendText = string.Empty;

    [ObservableProperty]
    private bool _sendAsHex = false;

    [ObservableProperty]
    private string _statisticsDisplay = "0 bytes";

    private readonly DispatcherQueue? _dispatcherQueue;

    public PortViewModel(
        string portName,
        ISerialPortService serialPortService,
        ILogFilterService logFilterService,
        DispatcherQueue? dispatcherQueue = null)
    {
        _portName = portName;
        _serialPortService = serialPortService;
        _logFilterService = logFilterService;
        _dispatcherQueue = dispatcherQueue;
    }

    public void UpdateStatistics(PortStatistics stats)
    {
        StatisticsDisplay = $"↓ {MainViewModel.FormatDataSize(stats.ReceivedBytes)} | ↑ {MainViewModel.FormatDataSize(stats.SentBytes)}";
    }

    public void AddLog(LogEntry entry)
    {
        Logs.Add(entry);

        if (_logFilterService.ShouldDisplay(entry))
        {
            FilteredLogs.Add(entry);
        }

        // Limit log count to prevent memory issues
        if (Logs.Count > 10000)
        {
            Logs.RemoveAt(0);
        }
        if (FilteredLogs.Count > 10000)
        {
            FilteredLogs.RemoveAt(0);
        }
    }

    [RelayCommand]
    private async Task SendDataAsync()
    {
        if (string.IsNullOrEmpty(SendText)) return;

        try
        {
            string displayContent;

            if (SendAsHex)
            {
                // Parse hex string to bytes
                var hexString = SendText.Replace(" ", "").Replace("-", "").Replace("0x", "").Replace("0X", "");
                if (hexString.Length % 2 != 0)
                {
                    // Invalid hex string length
                    return;
                }

                var bytes = new byte[hexString.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (!byte.TryParse(hexString.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                    {
                        // Invalid hex character
                        return;
                    }
                }

                await _serialPortService.SendDataAsync(PortName, bytes);
                displayContent = $"[HEX] {BitConverter.ToString(bytes).Replace("-", " ")}";
            }
            else
            {
                await _serialPortService.SendTextAsync(PortName, SendText, Encoding.UTF8);
                displayContent = SendText;
            }

            var logEntry = new LogEntry
            {
                PortName = PortName,
                Content = displayContent,
                IsReceived = false
            };

            AddLog(logEntry);
            SendText = string.Empty;
        }
        catch (Exception)
        {
            // Error will be handled by the service
        }
    }

}

/// <summary>
/// 垃圾数据检测工具类
/// </summary>
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

        // 检查是否包含过多不可打印字符
        var unprintableCount = line.Count(c => c < 32 && c != '\r' && c != '\n' && c != '\t');
        if (line.Length > 0 && (double)unprintableCount / line.Length > 0.5)
            return true;

        // 检查是否为大量重复字符
        if (line.Length > 10)
        {
            var distinctChars = line.Distinct().Count();
            if (distinctChars <= 2) // 只有1-2种不同字符
                return true;
        }

        // 检查是否为典型的乱码模式
        if (line.Length > 20)
        {
            var patternCount = 0;
            // 检查连续的不可读字符序列
            for (int i = 0; i < line.Length - 3; i++)
            {
                if (line[i] < 32 || line[i] > 126)
                {
                    patternCount++;
                    if (patternCount > line.Length * 0.3)
                        return true;
                }
            }
        }

        // 检查是否包含过多的扩展ASCII字符（可能是编码错误的UTF-8）
        var extendedAsciiCount = line.Count(c => c > 127 && c < 256);
        if (line.Length > 0 && (double)extendedAsciiCount / line.Length > 0.7)
            return true;

        return false;
    }
}