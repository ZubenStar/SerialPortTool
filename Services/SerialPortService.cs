using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<PortStateChangedEventArgs>? PortStateChanged;
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;

    public SerialPortService(ILogger<SerialPortService> logger)
    {
        _logger = logger;
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
            var portInstance = new PortInstance(config, _logger);
            
            // Subscribe to events
            portInstance.DataReceived += OnPortDataReceived;
            portInstance.ErrorOccurred += OnPortError;

            // Open the port
            var opened = await portInstance.OpenAsync();
            
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
                await portInstance.CloseAsync();
                RaisePortStateChanged(portName, ConnectionState.Connected, ConnectionState.Disconnected);
                _logger.LogInformation("Port {PortName} closed", portName);
                
                portInstance.DataReceived -= OnPortDataReceived;
                portInstance.ErrorOccurred -= OnPortError;
                portInstance.Dispose();
                
                // Wait for OS to fully release the port resources
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing port {PortName}", portName);
            }
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

        public SerialPortConfig Config { get; }
        public PortStatistics Statistics { get; } = new();
        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public PortInstance(SerialPortConfig config, ILogger logger)
        {
            Config = config;
            _logger = logger;
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
                    return;

                var port = _serialPort;
                _serialPort = null;

                try
                {
                    // Unsubscribe from events first to prevent callbacks during disposal
                    try
                    {
                        port.DataReceived -= SerialPort_DataReceived;
                        port.ErrorReceived -= SerialPort_ErrorReceived;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error unsubscribing from events");
                    }

                    // Close the port if it's open
                    if (port.IsOpen)
                    {
                        try
                        {
                            port.Close();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Error closing port");
                        }
                    }
                    
                    // Give the port time to fully close before disposing
                    Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during port close operations");
                }
                finally
                {
                    // Always try to dispose, catching the known .NET SerialPort bug
                    try
                    {
                        port.Dispose();
                    }
                    catch (NullReferenceException)
                    {
                        // Known .NET bug in SerialPort.Dispose() - safe to ignore
                        // The port resources are still released despite the exception
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error disposing port");
                    }

                    Statistics.DisconnectedAt = DateTime.Now;
                }
            });
        }

        public async Task ReconnectAsync()
        {
            await CloseAsync();
            await Task.Delay(200); // Wait for OS to release port resources
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

                    DataReceived?.Invoke(this, new DataReceivedEventArgs
                    {
                        PortName = Config.PortName,
                        Data = buffer
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading data from {PortName}", Config.PortName);
                RaiseError(ex);
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

                    // Unsubscribe from events
                    try
                    {
                        port.DataReceived -= SerialPort_DataReceived;
                        port.ErrorReceived -= SerialPort_ErrorReceived;
                    }
                    catch
                    {
                        // Ignore event unsubscription errors
                    }
                    
                    // Try to close if still open
                    if (port.IsOpen)
                    {
                        try
                        {
                            port.Close();
                        }
                        catch
                        {
                            // Ignore close errors during dispose
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
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Exception during port disposal");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during Dispose");
            }
            finally
            {
                _writeLock.Dispose();
            }
        }
    }
}