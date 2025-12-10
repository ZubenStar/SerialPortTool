using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SerialPortTool.Core.Enums;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 串口服务实现
/// </summary>
public class SerialPortService : ISerialPortService, IDisposable
{
    private readonly ILogger<SerialPortService> _logger;
    private readonly ConcurrentDictionary<string, PortInstance> _ports = new();
    private readonly IDataValidationService? _dataValidationService;
    private readonly IBaudRateDetectorService? _baudRateDetectorService;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<PortStateChangedEventArgs>? PortStateChanged;
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;
    public event EventHandler<BaudRateDetectionRequestedEventArgs>? BaudRateDetectionRequested;

    public SerialPortService(ILogger<SerialPortService> logger,
        IDataValidationService? dataValidationService = null,
        IBaudRateDetectorService? baudRateDetectorService = null)
    {
        _logger = logger;
        _dataValidationService = dataValidationService;
        _baudRateDetectorService = baudRateDetectorService;
    }

    public Task<IEnumerable<string>> GetAvailablePortsAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                var ports = SerialPort.GetPortNames();
                _logger.LogInformation("Found {Count} available ports", ports.Length);
                return ports.AsEnumerable();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available ports");
                return Enumerable.Empty<string>();
            }
        });
    }

    public async Task<bool> OpenPortAsync(SerialPortConfig config)
    {
        if (string.IsNullOrEmpty(config.PortName))
        {
            _logger.LogWarning("Port name is empty");
            return false;
        }

        if (_ports.ContainsKey(config.PortName))
        {
            _logger.LogWarning("Port {PortName} is already open", config.PortName);
            return false;
        }

        try
        {
            // Verify port is actually available before attempting to open
            var availablePorts = await GetAvailablePortsAsync();
            if (!availablePorts.Contains(config.PortName))
            {
                _logger.LogWarning("Port {PortName} is not available in the system", config.PortName);
                return false;
            }

            var portInstance = new PortInstance(config, _logger, _dataValidationService, _baudRateDetectorService, this);
            
            // Subscribe to events
            portInstance.DataReceived += OnPortDataReceived;
            portInstance.ErrorOccurred += OnPortError;

            // Open the port with retry logic
            bool opened = false;
            int retryCount = 0;
            const int maxRetries = 3;
            
            while (!opened && retryCount < maxRetries)
            {
                opened = await portInstance.OpenAsync();
                
                if (!opened)
                {
                    retryCount++;
                    _logger.LogWarning("Failed to open port {PortName} (attempt {Attempt}/{Max})",
                        config.PortName, retryCount, maxRetries);
                    
                    if (retryCount < maxRetries)
                    {
                        // Wait before retry to allow OS to release resources
                        await Task.Delay(300);
                    }
                }
            }
            
            if (opened)
            {
                _ports[config.PortName] = portInstance;
                RaisePortStateChanged(config.PortName, ConnectionState.Disconnected, ConnectionState.Connected);
                _logger.LogInformation("Port {PortName} opened successfully", config.PortName);
                return true;
            }
            else
            {
                portInstance.Dispose();
                _logger.LogError("Failed to open port {PortName} after {MaxRetries} attempts",
                    config.PortName, maxRetries);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error opening port {PortName}", config.PortName);
            RaiseError(config.PortName, ex);
            return false;
        }
    }

    public async Task ClosePortAsync(string portName)
    {
        if (_ports.TryRemove(portName, out var portInstance))
        {
            try
            {
                _logger.LogInformation("Starting close sequence for port {PortName}", portName);
                
                // Unsubscribe from events first to prevent callbacks during disposal
                try
                {
                    portInstance.DataReceived -= OnPortDataReceived;
                    portInstance.ErrorOccurred -= OnPortError;
                    _logger.LogDebug("Unsubscribed from port {PortName} events", portName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing from port {PortName} events", portName);
                }
                
                // Close the port with enhanced cleanup
                await portInstance.CloseAsync();
                
                RaisePortStateChanged(portName, ConnectionState.Connected, ConnectionState.Disconnected);
                _logger.LogInformation("Port {PortName} closed successfully", portName);
                
                // Dispose the instance
                try
                {
                    portInstance.Dispose();
                    _logger.LogDebug("Port {PortName} instance disposed", portName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing port {PortName} instance", portName);
                }
                
                // Critical: Wait longer for Windows to fully release the COM port handle
                // Windows can take 500-1000ms to fully release serial port resources
                await Task.Delay(500);
                
                // Verify port is actually released by attempting to check its availability
                try
                {
                    var availablePorts = await GetAvailablePortsAsync();
                    var isAvailable = availablePorts.Contains(portName);
                    _logger.LogInformation("Port {PortName} release verified: Available={IsAvailable}",
                        portName, isAvailable);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not verify port {PortName} availability", portName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing port {PortName}", portName);
                
                // Critical: Even on error, ensure we wait for OS cleanup
                // This prevents the port from being stuck in a bad state
                await Task.Delay(500);
                
                // Try to force cleanup by triggering garbage collection
                // This can help release unmanaged resources
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                
                _logger.LogWarning("Forced garbage collection after error closing port {PortName}", portName);
            }
        }
        else
        {
            _logger.LogWarning("Attempted to close port {PortName} but it was not in the open ports collection", portName);
        }
    }

    public async Task<int> OpenAllPortsAsync(SerialPortConfig defaultConfig)
    {
        var availablePorts = await GetAvailablePortsAsync();
        var openedCount = 0;

        foreach (var portName in availablePorts)
        {
            // Skip ports that are already open
            if (_ports.ContainsKey(portName))
            {
                _logger.LogInformation("Port {PortName} is already open, skipping", portName);
                continue;
            }

            try
            {
                var config = new SerialPortConfig
                {
                    PortName = portName,
                    BaudRate = defaultConfig.BaudRate,
                    DataBits = defaultConfig.DataBits,
                    StopBits = defaultConfig.StopBits,
                    Parity = defaultConfig.Parity,
                    ReadTimeout = defaultConfig.ReadTimeout,
                    WriteTimeout = defaultConfig.WriteTimeout,
                    AutoReconnect = defaultConfig.AutoReconnect,
                    ReconnectInterval = defaultConfig.ReconnectInterval
                };

                var opened = await OpenPortAsync(config);
                if (opened)
                {
                    openedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error opening port {PortName} during batch open", portName);
            }
        }

        _logger.LogInformation("Opened {Count} ports out of {Total} available", openedCount, availablePorts.Count());
        return openedCount;
    }

    public async Task CloseAllPortsAsync()
    {
        var closeTasks = _ports.Keys.Select(ClosePortAsync).ToList();
        await Task.WhenAll(closeTasks);
        _logger.LogInformation("All ports closed");
    }

    public bool IsPortOpen(string portName)
    {
        return _ports.TryGetValue(portName, out var port) && port.IsOpen;
    }

    public async Task SendDataAsync(string portName, byte[] data)
    {
        if (_ports.TryGetValue(portName, out var port))
        {
            try
            {
                await port.SendDataAsync(data);
                _logger.LogDebug("Sent {Count} bytes to {PortName}", data.Length, portName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending data to {PortName}", portName);
                RaiseError(portName, ex);
                throw;
            }
        }
        else
        {
            throw new InvalidOperationException($"Port {portName} is not open");
        }
    }

    public async Task SendTextAsync(string portName, string text, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        var data = encoding.GetBytes(text);
        await SendDataAsync(portName, data);
    }

    public SerialPortConfig? GetPortConfig(string portName)
    {
        return _ports.TryGetValue(portName, out var port) ? port.Config : null;
    }

    public IEnumerable<string> GetOpenPorts()
    {
        return _ports.Keys.ToList();
    }

    public PortStatistics GetStatistics(string portName)
    {
        if (_ports.TryGetValue(portName, out var port))
        {
            return port.Statistics;
        }
        return new PortStatistics { PortName = portName };
    }

    private void OnPortDataReceived(object? sender, DataReceivedEventArgs e)
    {
        DataReceived?.Invoke(this, e);
    }

    private void OnPortError(object? sender, ErrorEventArgs e)
    {
        ErrorOccurred?.Invoke(this, e);
        
        // Auto reconnect if enabled
        if (_ports.TryGetValue(e.PortName, out var port) && port.Config.AutoReconnect)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(port.Config.ReconnectInterval);
                await TryReconnectAsync(e.PortName);
            });
        }
    }

    private async Task TryReconnectAsync(string portName)
    {
        if (_ports.TryGetValue(portName, out var port))
        {
            try
            {
                _logger.LogInformation("Attempting to reconnect {PortName}", portName);
                await port.ReconnectAsync();
                RaisePortStateChanged(portName, ConnectionState.Error, ConnectionState.Connected);
                _logger.LogInformation("Port {PortName} reconnected successfully", portName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect {PortName}", portName);
            }
        }
    }

    private void RaisePortStateChanged(string portName, ConnectionState oldState, ConnectionState newState)
    {
        PortStateChanged?.Invoke(this, new PortStateChangedEventArgs
        {
            PortName = portName,
            OldState = oldState,
            NewState = newState
        });
    }

    private void RaiseError(string portName, Exception exception)
    {
        ErrorOccurred?.Invoke(this, new ErrorEventArgs
        {
            PortName = portName,
            Exception = exception,
            ErrorMessage = exception.Message
        });
    }

    public void Dispose()
    {
        CloseAllPortsAsync().Wait();
        _ports.Clear();
    }

    /// <summary>
    /// 单个串口实例
    /// </summary>
    private class PortInstance : IDisposable
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ILogger _logger;
        private readonly IDataValidationService? _dataValidationService;
        private readonly IBaudRateDetectorService? _baudRateDetectorService;
        private readonly SerialPortService _parentService;

        public SerialPortConfig Config { get; }
        public PortStatistics Statistics { get; } = new();
        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public PortInstance(SerialPortConfig config, ILogger logger,
            IDataValidationService? dataValidationService,
            IBaudRateDetectorService? baudRateDetectorService,
            SerialPortService parentService)
        {
            Config = config;
            _logger = logger;
            _dataValidationService = dataValidationService;
            _baudRateDetectorService = baudRateDetectorService;
            _parentService = parentService;
            Statistics.PortName = config.PortName;
        }

        public Task<bool> OpenAsync()
        {
            return Task.Run(() =>
            {
                SerialPort? port = null;
                try
                {
                    // Dispose existing port if any
                    if (_serialPort != null)
                    {
                        var oldPort = _serialPort;
                        _serialPort = null;
                        
                        try
                        {
                            // Unsubscribe events first
                            try
                            {
                                oldPort.DataReceived -= SerialPort_DataReceived;
                                oldPort.ErrorReceived -= SerialPort_ErrorReceived;
                            }
                            catch { }
                            
                            // Close if open
                            if (oldPort.IsOpen)
                            {
                                try { oldPort.Close(); } catch { }
                            }
                            
                            // Dispose - catch known .NET bug
                            try
                            {
                                oldPort.Dispose();
                            }
                            catch (NullReferenceException)
                            {
                                // Known .NET SerialPort bug - safe to ignore
                            }
                        }
                        catch
                        {
                            // Ignore all disposal errors
                        }
                    }

                    // Create new port instance
                    port = new SerialPort
                    {
                        PortName = Config.PortName,
                        BaudRate = Config.BaudRate,
                        DataBits = Config.DataBits,
                        StopBits = Config.StopBits,
                        Parity = Config.Parity,
                        ReadTimeout = Config.ReadTimeout,
                        WriteTimeout = Config.WriteTimeout
                    };

                    // Attach event handlers before opening
                    port.DataReceived += SerialPort_DataReceived;
                    port.ErrorReceived += SerialPort_ErrorReceived;

                    // Open the port
                    port.Open();
                    
                    // Only assign to field after successful open
                    _serialPort = port;
                    Statistics.ConnectedAt = DateTime.Now;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open port {PortName}", Config.PortName);
                    
                    // Clean up the local port instance if it was created
                    if (port != null)
                    {
                        try
                        {
                            port.DataReceived -= SerialPort_DataReceived;
                            port.ErrorReceived -= SerialPort_ErrorReceived;
                            
                            if (port.IsOpen)
                            {
                                port.Close();
                            }
                            
                            port.Dispose();
                        }
                        catch
                        {
                            // Ignore cleanup errors
                        }
                    }
                    
                    // Ensure field is null on failure
                    _serialPort = null;
                    
                    RaiseError(ex);
                    return false;
                }
            });
        }

        public Task CloseAsync()
        {
            return Task.Run(() =>
            {
                if (_serialPort == null)
                {
                    _logger.LogDebug("CloseAsync called but _serialPort is already null");
                    return;
                }

                var port = _serialPort;
                _serialPort = null;

                try
                {
                    _logger.LogDebug("Starting close sequence for port {PortName}", Config.PortName);
                    
                    // Step 1: Unsubscribe from events first to prevent callbacks during disposal
                    try
                    {
                        port.DataReceived -= SerialPort_DataReceived;
                        port.ErrorReceived -= SerialPort_ErrorReceived;
                        _logger.LogDebug("Unsubscribed from events for port {PortName}", Config.PortName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error unsubscribing from events for port {PortName}", Config.PortName);
                    }

                    // Step 2: Discard any buffered data to prevent blocking
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.DiscardInBuffer();
                            port.DiscardOutBuffer();
                            _logger.LogDebug("Discarded buffers for port {PortName}", Config.PortName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error discarding buffers for port {PortName}", Config.PortName);
                    }

                    // Step 3: Close the port if it's open with enhanced retry logic
                    if (port.IsOpen)
                    {
                        int retryCount = 0;
                        const int maxRetries = 5; // Increased from 3 to 5
                        bool closed = false;
                        
                        while (!closed && retryCount < maxRetries)
                        {
                            try
                            {
                                // Try to close the port
                                port.Close();
                                closed = true;
                                _logger.LogDebug("Port {PortName} closed successfully on attempt {Attempt}",
                                    Config.PortName, retryCount + 1);
                            }
                            catch (UnauthorizedAccessException ex)
                            {
                                // Port is locked by another process or thread
                                retryCount++;
                                _logger.LogWarning(ex, "Port {PortName} is locked (attempt {Attempt}/{Max})",
                                    Config.PortName, retryCount, maxRetries);
                                
                                if (retryCount < maxRetries)
                                {
                                    // Wait longer between retries for locked ports
                                    Thread.Sleep(200);
                                }
                            }
                            catch (IOException ex)
                            {
                                // I/O error during close
                                retryCount++;
                                _logger.LogWarning(ex, "I/O error closing port {PortName} (attempt {Attempt}/{Max})",
                                    Config.PortName, retryCount, maxRetries);
                                
                                if (retryCount < maxRetries)
                                {
                                    Thread.Sleep(150);
                                }
                            }
                            catch (Exception ex)
                            {
                                retryCount++;
                                _logger.LogWarning(ex, "Error closing port {PortName} (attempt {Attempt}/{Max})",
                                    Config.PortName, retryCount, maxRetries);
                                
                                if (retryCount < maxRetries)
                                {
                                    Thread.Sleep(100);
                                }
                            }
                        }
                        
                        if (!closed)
                        {
                            _logger.LogError("Failed to close port {PortName} after {MaxRetries} attempts",
                                Config.PortName, maxRetries);
                        }
                    }
                    
                    // Step 4: Critical delay - Give Windows time to release the COM port handle
                    // Windows needs 200-300ms to fully release serial port resources
                    Thread.Sleep(250);
                    
                    _logger.LogDebug("Port {PortName} close operations completed", Config.PortName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical error during port {PortName} close operations", Config.PortName);
                }
                finally
                {
                    // Step 5: Always try to dispose, catching the known .NET SerialPort bug
                    try
                    {
                        port.Dispose();
                        _logger.LogDebug("Port {PortName} disposed successfully", Config.PortName);
                    }
                    catch (NullReferenceException)
                    {
                        // Known .NET bug in SerialPort.Dispose() - safe to ignore
                        // The port resources are still released despite the exception
                        _logger.LogDebug("Caught known NullReferenceException in SerialPort.Dispose() for port {PortName}",
                            Config.PortName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing port {PortName}", Config.PortName);
                    }
                    
                    // Step 6: Final delay after disposal to ensure complete OS cleanup
                    // This is critical for reliable port reopening
                    Thread.Sleep(150);

                    Statistics.DisconnectedAt = DateTime.Now;
                    
                    _logger.LogInformation("Port {PortName} fully closed and resources released", Config.PortName);
                }
            });
        }

        public async Task ReconnectAsync()
        {
            await CloseAsync();
            await Task.Delay(500); // Wait longer for OS to release port resources
            await OpenAsync();
        }

        public async Task SendDataAsync(byte[] data)
        {
            await _writeLock.WaitAsync();
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    await _serialPort.BaseStream.WriteAsync(data, 0, data.Length);
                    Statistics.SentBytes += data.Length;
                    Statistics.SentMessages++;
                }
                else
                {
                    throw new InvalidOperationException($"Port {Config.PortName} is not open");
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort?.IsOpen == true && _serialPort.BytesToRead > 0)
                {
                    var buffer = new byte[_serialPort.BytesToRead];
                    var bytesRead = _serialPort.Read(buffer, 0, buffer.Length);

                    Statistics.ReceivedBytes += bytesRead;
                    Statistics.ReceivedMessages++;

                    // 如果有数据验证服务，先验证数据
                    if (_dataValidationService != null)
                    {
                        ProcessDataWithValidation(buffer);
                    }
                    else
                    {
                        // 传统处理方式
                        DataReceived?.Invoke(this, new DataReceivedEventArgs
                        {
                            PortName = Config.PortName,
                            Data = buffer
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading data from {PortName}", Config.PortName);
                RaiseError(ex);
            }
        }

        private async void ProcessDataWithValidation(byte[] buffer)
        {
            try
            {
                var validationResult = await _dataValidationService!.ValidateDataAsync(buffer, Config.PortName);
                
                _logger.LogTrace("Data validation for {PortName}: Valid={IsValid}, Score={Score}, Action={Action}",
                    Config.PortName, validationResult.IsValid, validationResult.QualityScore, validationResult.SuggestedAction);

                switch (validationResult.SuggestedAction)
                {
                    case ValidationAction.Normal:
                        DataReceived?.Invoke(this, new DataReceivedEventArgs
                        {
                            PortName = Config.PortName,
                            Data = buffer
                        });
                        break;

                    case ValidationAction.CleanAndProcess:
                        if (validationResult.ProcessedData != null)
                        {
                            DataReceived?.Invoke(this, new DataReceivedEventArgs
                            {
                                PortName = Config.PortName,
                                Data = validationResult.ProcessedData
                            });
                        }
                        break;

                    case ValidationAction.Discard:
                        _logger.LogDebug("Discarding invalid data from {PortName}: {Message}",
                            Config.PortName, validationResult.Message);
                        break;

                    case ValidationAction.TriggerBaudRateDetection:
                        _logger.LogWarning("Triggering baud rate detection for {PortName}: {Message}",
                            Config.PortName, validationResult.Message);
                        
                        // 触发波特率检测事件
                        _parentService.BaudRateDetectionRequested?.Invoke(_parentService, new BaudRateDetectionRequestedEventArgs
                        {
                            PortName = Config.PortName,
                            CurrentBaudRate = Config.BaudRate,
                            Reason = validationResult.Message
                        });
                        break;

                    case ValidationAction.PauseProcessing:
                        _logger.LogWarning("Pausing data processing for {PortName}: {Message}",
                            Config.PortName, validationResult.Message);
                        
                        // 暂停一段时间后恢复
                        await Task.Delay(1000);
                        break;
                }

                // 检查是否需要触发波特率检测
                if (await _dataValidationService.ShouldTriggerBaudRateDetectionAsync(Config.PortName))
                {
                    _parentService.BaudRateDetectionRequested?.Invoke(_parentService, new BaudRateDetectionRequestedEventArgs
                    {
                        PortName = Config.PortName,
                        CurrentBaudRate = Config.BaudRate,
                        Reason = "数据质量持续较差，建议重新检测波特率"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in data validation for {PortName}", Config.PortName);
                
                // 验证失败时，仍然发送原始数据以确保不丢失数据
                DataReceived?.Invoke(this, new DataReceivedEventArgs
                {
                    PortName = Config.PortName,
                    Data = buffer
                });
            }
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            _logger.LogWarning("Serial error on {PortName}: {ErrorType}", Config.PortName, e.EventType);
            Statistics.ErrorCount++;
            RaiseError(new Exception($"Serial error: {e.EventType}"));
        }

        private void RaiseError(Exception exception)
        {
            ErrorOccurred?.Invoke(this, new ErrorEventArgs
            {
                PortName = Config.PortName,
                Exception = exception,
                ErrorMessage = exception.Message
            });
        }

        public void Dispose()
        {
            try
            {
                if (_serialPort != null)
                {
                    var port = _serialPort;
                    _serialPort = null;

                    _logger.LogDebug("Disposing port {PortName}", Config.PortName);

                    // Unsubscribe from events
                    try
                    {
                        port.DataReceived -= SerialPort_DataReceived;
                        port.ErrorReceived -= SerialPort_ErrorReceived;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error unsubscribing events during dispose for port {PortName}",
                            Config.PortName);
                    }
                    
                    // Discard buffers before closing
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.DiscardInBuffer();
                            port.DiscardOutBuffer();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error discarding buffers during dispose for port {PortName}",
                            Config.PortName);
                    }
                    
                    // Try to close if still open
                    if (port.IsOpen)
                    {
                        try
                        {
                            port.Close();
                            // Give time for close to complete
                            Thread.Sleep(100);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error closing port during dispose for port {PortName}",
                                Config.PortName);
                        }
                    }
                    
                    // Dispose the port - catch known .NET SerialPort bug
                    try
                    {
                        port.Dispose();
                    }
                    catch (NullReferenceException)
                    {
                        // Known .NET bug in SerialPort.Dispose() - safe to ignore
                        _logger.LogDebug("Caught known NullReferenceException in SerialPort.Dispose() for port {PortName}",
                            Config.PortName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Exception during port disposal for port {PortName}",
                            Config.PortName);
                    }
                    
                    // Final delay to ensure cleanup
                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during Dispose for port {PortName}", Config.PortName);
            }
            finally
            {
                try
                {
                    _writeLock.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing write lock for port {PortName}", Config.PortName);
                }
            }
        }
    }
}