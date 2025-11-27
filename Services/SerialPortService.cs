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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing port {PortName}", portName);
            }
        }
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
                try
                {
                    _serialPort = new SerialPort
                    {
                        PortName = Config.PortName,
                        BaudRate = Config.BaudRate,
                        DataBits = Config.DataBits,
                        StopBits = Config.StopBits,
                        Parity = Config.Parity,
                        ReadTimeout = Config.ReadTimeout,
                        WriteTimeout = Config.WriteTimeout
                    };

                    _serialPort.DataReceived += SerialPort_DataReceived;
                    _serialPort.ErrorReceived += SerialPort_ErrorReceived;

                    _serialPort.Open();
                    Statistics.ConnectedAt = DateTime.Now;
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to open port {PortName}", Config.PortName);
                    RaiseError(ex);
                    return false;
                }
            });
        }

        public Task CloseAsync()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (_serialPort?.IsOpen == true)
                    {
                        _serialPort.Close();
                        Statistics.DisconnectedAt = DateTime.Now;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing port {PortName}", Config.PortName);
                }
            });
        }

        public async Task ReconnectAsync()
        {
            await CloseAsync();
            await Task.Delay(500); // Wait before reconnecting
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
            _serialPort?.Dispose();
            _writeLock.Dispose();
        }
    }
}