using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SerialPortTool.ViewModels;

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
    private string _title = "串口工具 - Multi-Port Serial Monitor";

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

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    private const int MaxLogCount = 5000; // Limit logs to prevent freezing

    public event EventHandler? ScrollToBottomRequested;

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
            var config = new SerialPortConfig
            {
                PortName = portName,
                BaudRate = BaudRate,
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
        DisplayLogs.Clear();
        
        var filtered = string.IsNullOrEmpty(SearchText)
            ? AllLogs
            : AllLogs.Where(log =>
                log.Content.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                log.PortName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var log in filtered)
        {
            DisplayLogs.Add(log);
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
        _dispatcherQueue.TryEnqueue(() =>
        {
            var text = Encoding.UTF8.GetString(e.Data);
            var logEntry = new LogEntry
            {
                PortName = e.PortName,
                Content = text,
                Level = Core.Enums.LogLevel.Info,
                RawData = e.Data,
                IsReceived = true
            };

            // Limit log count to prevent memory issues and UI freezing
            if (AllLogs.Count >= MaxLogCount)
            {
                AllLogs.RemoveAt(0);
                if (DisplayLogs.Count > 0)
                {
                    DisplayLogs.RemoveAt(0);
                }
            }

            AllLogs.Add(logEntry);
            
            // Apply search filter
            if (string.IsNullOrEmpty(SearchText) ||
                logEntry.Content.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                logEntry.PortName.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                DisplayLogs.Add(logEntry);
            }

            // Write to log file
            _ = _fileLoggerService.WriteLogAsync(e.PortName, logEntry);

            // Update port-specific logs and statistics
            var portVm = OpenPorts.FirstOrDefault(p => p.PortName == e.PortName);
            if (portVm != null)
            {
                portVm.AddLog(logEntry);
                // Update statistics from service
                portVm.UpdateStatistics(_serialPortService.GetStatistics(e.PortName));
            }

            // Trigger auto-scroll if enabled
            if (AutoScroll)
            {
                ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    private void OnPortStateChanged(object? sender, PortStateChangedEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusMessage = $"Port {e.PortName}: {e.NewState}";
            _logger.LogInformation("Port {PortName} state changed: {OldState} -> {NewState}",
                e.PortName, e.OldState, e.NewState);
        });
    }

    private void OnErrorOccurred(object? sender, Services.ErrorEventArgs e)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            StatusMessage = $"Error on {e.PortName}: {e.ErrorMessage}";
            _logger.LogError(e.Exception, "Error on port {PortName}", e.PortName);
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
        StatisticsDisplay = $"↓ {stats.ReceivedBytes} bytes | ↑ {stats.SentBytes} bytes";
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