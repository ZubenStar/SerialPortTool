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
    private readonly ISerialPortService _serialPortService;
    private readonly ILogFilterService _logFilterService;
    private readonly IFileLoggerService _fileLoggerService;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherQueue _dispatcherQueue;

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
    private ObservableCollection<LogEntry> _allLogs = new();

    [ObservableProperty]
    private ObservableCollection<LogEntry> _displayLogs = new();

    [ObservableProperty]
    private ObservableCollection<FilterRule> _filters = new();

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
                
                // Debounce filter updates to reduce UI thrashing
                _filterDebounceTimer?.Dispose();
                _filterDebounceTimer = new System.Threading.Timer(_ =>
                {
                    _dispatcherQueue.TryEnqueue(() => FilterLogs());
                }, null, 150, System.Threading.Timeout.Infinite);
            }
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

    private const int MaxDisplayLogs = 1000; // Limit displayed logs for performance

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
        ILogger<MainViewModel> logger)
    {
        _serialPortService = serialPortService;
        _logFilterService = logFilterService;
        _fileLoggerService = fileLoggerService;
        _settingsService = settingsService;
        _logger = logger;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        // Subscribe to events
        _serialPortService.DataReceived += OnDataReceived;
        _serialPortService.PortStateChanged += OnPortStateChanged;
        _serialPortService.ErrorOccurred += OnErrorOccurred;

        // Initialize
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Load saved baud rate
        BaudRate = await _settingsService.LoadSettingAsync("BaudRate", 3000000);
        _logger.LogInformation("Loaded baud rate: {BaudRate}", BaudRate);

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
                }
                else
                {
                    StatusMessage = "Invalid custom baud rate";
                    return;
                }
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
                
                // Save the baud rate for next time
                await _settingsService.SaveSettingAsync("BaudRate", BaudRate);
                
                StatusMessage = $"Port {portName} opened successfully. Log file: {_fileLoggerService.GetLogFilePath(portName)}";
                _logger.LogInformation("Port {PortName} opened with logging", portName);
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
    private void ClearLogs()
    {
        AllLogs.Clear();
        DisplayLogs.Clear();
        _logger.LogInformation("All logs cleared");
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

    [RelayCommand]
    private async Task ClosePortAsync(string portName)
    {
        try
        {
            // Stop file logging
            await _fileLoggerService.StopLoggingAsync(portName);
            
            await _serialPortService.ClosePortAsync(portName);
            var portVm = OpenPorts.FirstOrDefault(p => p.PortName == portName);
            if (portVm != null)
            {
                OpenPorts.Remove(portVm);
            }
            StatusMessage = $"Port {portName} closed";
            _logger.LogInformation("Port {PortName} closed", portName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error closing port {portName}: {ex.Message}";
            _logger.LogError(ex, "Error closing port {PortName}", portName);
        }
    }

    private void OnDataReceived(object? sender, DataReceivedEventArgs e)
    {
        // Capture data on background thread
        var text = Encoding.UTF8.GetString(e.Data);
        var portName = e.PortName;
        
        // Build log entries on background thread
        var newLogs = new List<LogEntry>();
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            
            // Skip only the last line if it's empty (common from splitting)
            if (i == lines.Length - 1 && string.IsNullOrEmpty(line))
                continue;
            
            var logEntry = new LogEntry
            {
                PortName = portName,
                Content = line,
                Level = Core.Enums.LogLevel.Info,
                RawData = Encoding.UTF8.GetBytes(line),
                IsReceived = true
            };
            
            newLogs.Add(logEntry);
            
            // Write to log file asynchronously
            _ = _fileLoggerService.WriteLogAsync(portName, logEntry);
        }

        if (newLogs.Count == 0) return;

        // Dispatch UI updates immediately to UI thread for real-time display
        _dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Add to AllLogs first
                foreach (var logEntry in newLogs)
                {
                    AllLogs.Add(logEntry);
                }
                
                // Apply regex filter to new logs before adding to DisplayLogs
                var logsToDisplay = newLogs;
                if (!string.IsNullOrEmpty(SearchText) && IsRegexValid)
                {
                    try
                    {
                        var regex = new Regex(SearchText, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                        logsToDisplay = newLogs.Where(log =>
                            regex.IsMatch(log.Content) ||
                            regex.IsMatch(log.PortName)).ToList();
                        
                        // Update match count
                        MatchCount = DisplayLogs.Count + logsToDisplay.Count;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error applying regex filter to new logs: {Pattern}", SearchText);
                        logsToDisplay = new List<LogEntry>();
                    }
                }
                
                // Add filtered logs to DisplayLogs
                foreach (var logEntry in logsToDisplay)
                {
                    DisplayLogs.Add(logEntry);
                }

                // Trim DisplayLogs to prevent memory issues
                while (DisplayLogs.Count > MaxDisplayLogs)
                {
                    DisplayLogs.RemoveAt(0);
                }
                
                // Trim AllLogs separately
                while (AllLogs.Count > MaxDisplayLogs * 2)
                {
                    AllLogs.RemoveAt(0);
                }

                // Update port-specific logs and statistics
                var portVm = OpenPorts.FirstOrDefault(p => p.PortName == portName);
                if (portVm != null)
                {
                    foreach (var logEntry in newLogs)
                    {
                        portVm.AddLog(logEntry);
                    }
                    
                    var stats = _serialPortService.GetStatistics(portName);
                    portVm.UpdateStatistics(stats);
                }
                
                // Auto-scroll to bottom
                if (AutoScroll)
                {
                    // ItemsRepeater will handle scrolling via UI binding
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating UI with received data");
            }
        });
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

    public string GetLogDirectory()
    {
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return System.IO.Path.Combine(documentsPath, "SerialPortTool", "Logs");
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
            await _serialPortService.SendTextAsync(PortName, SendText, Encoding.UTF8);

            var logEntry = new LogEntry
            {
                PortName = PortName,
                Content = SendText,
                Level = Core.Enums.LogLevel.Info,
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

    [RelayCommand]
    private void ClearLogs()
    {
        Logs.Clear();
        FilteredLogs.Clear();
    }
}