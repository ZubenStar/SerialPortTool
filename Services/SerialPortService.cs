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
public class SerialPortService : ISerialPortService, IDisposable, IAsyncDisposable
{
    private readonly ILogger<SerialPortService> _logger;
    private readonly ConcurrentDictionary<string, PortInstance> _ports = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastReconnectAttempt = new();
    private readonly IDataValidationService? _dataValidationService;
    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(3);

    /// <summary>Set once, from the teardown path. Guards double teardown, not new opens.</summary>
    private volatile bool _disposed;

    public event EventHandler<DataReceivedEventArgs>? DataReceived;
    public event EventHandler<PortStateChangedEventArgs>? PortStateChanged;
    public event EventHandler<ErrorEventArgs>? ErrorOccurred;
    public event EventHandler<BaudRateDetectionRequestedEventArgs>? BaudRateDetectionRequested;

    // The old constructor also took an IBaudRateDetectorService and threaded it down to every
    // PortInstance, where it was stored and never read — baud-rate detection is driven from
    // MainViewModel, which owns the "close the port, probe, reopen" flow. The parameter is gone so the
    // dependency is no longer implied.
    public SerialPortService(ILogger<SerialPortService> logger,
        IDataValidationService? dataValidationService = null)
    {
        _logger = logger;
        _dataValidationService = dataValidationService;
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
            // Retry availability check to handle OS handle release delay after recent close
            var availablePorts = (await GetAvailablePortsAsync()).ToList();
            if (!availablePorts.Contains(config.PortName))
            {
                _logger.LogInformation("Port {PortName} not immediately available, waiting for OS to release handle", config.PortName);

                // Wait for OS to release the serial port handle (classic Windows SerialPort issue)
                for (int waitAttempt = 0; waitAttempt < 3 && !availablePorts.Contains(config.PortName); waitAttempt++)
                {
                    await Task.Delay(500);
                    availablePorts = (await GetAvailablePortsAsync()).ToList();
                    _logger.LogDebug("Port availability recheck (attempt {Attempt}/3): {Available}",
                        waitAttempt + 1, string.Join(", ", availablePorts));
                }

                if (!availablePorts.Contains(config.PortName))
                {
                    _logger.LogWarning("Port {PortName} is not available in the system after waiting", config.PortName);
                    return false;
                }

                _logger.LogInformation("Port {PortName} became available after waiting for handle release", config.PortName);
            }

            var portInstance = new PortInstance(config, _logger, _dataValidationService, this);
            
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
                        // Wait longer before retry to allow OS to release resources
                        await Task.Delay(500);
                    }
                }
            }
            
            if (opened)
            {
                // TryAdd, not the indexer: the ContainsKey check at the top of this method is separated
                // from here by several awaits (availability probe, up to 3 open attempts with 500 ms
                // backoff), so two concurrent open calls for the same port can both get this far. With
                // the indexer the loser's instance was overwritten and dropped — an open SerialPort
                // that nothing ever disposes. The loser is closed here instead.
                if (!_ports.TryAdd(config.PortName, portInstance))
                {
                    _logger.LogWarning(
                        "Port {PortName} was already registered by a concurrent open; discarding the duplicate instance",
                        config.PortName);
                    try
                    {
                        portInstance.BeginShutdown();
                        portInstance.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error disposing the duplicate instance for port {PortName}", config.PortName);
                    }

                    return false;
                }

                RaisePortStateChanged(config.PortName, ConnectionState.Disconnected, ConnectionState.Connected);
                _logger.LogInformation("Port {PortName} opened successfully", config.PortName);
                return true;
            }
            else
            {
                portInstance.BeginShutdown();
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
                _logger.LogInformation("Closing port {PortName}", portName);

                // Unsubscribe from events first to prevent callbacks during disposal
                try
                {
                    portInstance.DataReceived -= OnPortDataReceived;
                    portInstance.ErrorOccurred -= OnPortError;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing from port {PortName} events", portName);
                }

                // Close the port (CloseAsync handles event teardown, buffer discard and
                // timeout-guarded dispose internally)
                await portInstance.CloseAsync();

                // Clear stale validation state so it doesn't affect next open
                _dataValidationService?.ResetValidationState(portName);

                // Clear reconnection cooldown so port can be reopened immediately
                _lastReconnectAttempt.TryRemove(portName, out _);

                RaisePortStateChanged(portName, ConnectionState.Connected, ConnectionState.Disconnected);
                _logger.LogInformation("Port {PortName} closed successfully", portName);

                // Dispose the instance
                try
                {
                    portInstance.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing port {PortName} instance", portName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing port {PortName}", portName);
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

        // Auto reconnect if enabled, with cooldown to prevent reconnect storm
        if (_ports.TryGetValue(e.PortName, out var port) && port.Config.AutoReconnect)
        {
            var now = DateTime.UtcNow;
            if (_lastReconnectAttempt.TryGetValue(e.PortName, out var lastAttempt) &&
                now - lastAttempt < ReconnectCooldown)
            {
                return; // Skip reconnect, still in cooldown
            }
            _lastReconnectAttempt[e.PortName] = now;

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

    /// <summary>
    /// Synchronous, best-effort teardown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The real path is <see cref="DisposeAsync"/>: the container is torn down through
    /// <c>ServiceProvider.DisposeAsync()</c> in <c>App.OnWindowClosed</c>, which prefers
    /// <see cref="IAsyncDisposable"/>. This method exists so a synchronously disposed container still
    /// closes the ports instead of leaking every COM handle.
    /// </para>
    /// <para>
    /// No <c>Task.Run(...).Wait(timeout)</c> here on purpose. <c>SerialPort.Close()</c> is already a
    /// synchronous call, so wrapping it in a task we then wait on with a timeout bounds nothing: the
    /// wait returns <c>false</c> and the task keeps running, so the port ends up being disposed while
    /// its own <c>Close()</c> is still in flight. That is where the "ports leak / the handle is used
    /// after close" class of bugs came from.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var portName in _ports.Keys.ToList())
        {
            DisposePortInstance(portName, "Dispose");
        }

        _ports.Clear();
    }

    /// <summary>
    /// Closes every port asynchronously, then disposes any instance that could not be closed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            await CloseAllPortsAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error during SerialPortService.DisposeAsync");
        }

        // Anything still present failed to close (or never got its turn). Dispose it synchronously
        // rather than dropping the reference: a dropped PortInstance is a SerialPort that is never
        // disposed, i.e. a leaked COM handle — which then shows up as "the port won't reopen".
        foreach (var portName in _ports.Keys.ToList())
        {
            DisposePortInstance(portName, "DisposeAsync");
        }

        _ports.Clear();
    }

    private void DisposePortInstance(string portName, string caller)
    {
        if (!_ports.TryRemove(portName, out var portInstance))
        {
            return;
        }

        try
        {
            // Stop in-flight sends first, then close: the other order lets a queued write land on a
            // half-disposed port.
            portInstance.BeginShutdown();
            portInstance.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error disposing port {PortName} during {Caller}", portName, caller);
        }
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
        private readonly SerialPortService _parentService;
        private volatile bool _isClosing = false; // Flag to prevent DataReceived during close

        // Cancels writes that are already waiting on / inside a send when the port is closing.
        // Null while the port is not open, so a send on a closed port fails fast with
        // InvalidOperationException instead of blocking on the write lock.
        //
        // Deliberately never disposed — see Dispose() below for why both this and _writeLock are
        // handed to the GC instead of being torn down.
        private CancellationTokenSource? _writeCts;

        // Reused scratch buffer for SerialPort.Read(...). The DataReceived event is delivered on a
        // single worker thread per port (the SerialPort internal "DataReceived" thread), and the
        // _isClosing flag gates re-entrance during shutdown — so a non-locked, grow-on-demand
        // buffer is safe and avoids allocating a fresh byte[] on every chunk under high baud
        // rates. The data that leaves this method via the DataReceived event is always a
        // freshly-allocated, exact-sized copy so consumers can hold onto it safely.
        private byte[] _readScratch = Array.Empty<byte>();

        // Tick count (Environment.TickCount64) until which incoming chunks are dropped after a
        // PauseProcessing verdict. Written and read by the same read thread only.
        private long _validationPauseUntilTicks;

        public SerialPortConfig Config { get; }
        public PortStatistics Statistics { get; } = new();
        public bool IsOpen => _serialPort?.IsOpen ?? false;

        public event EventHandler<DataReceivedEventArgs>? DataReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public PortInstance(SerialPortConfig config, ILogger logger,
            IDataValidationService? dataValidationService,
            SerialPortService parentService)
        {
            Config = config;
            _logger = logger;
            _dataValidationService = dataValidationService;
            _parentService = parentService;
            Statistics.PortName = config.PortName;
        }

        public Task<bool> OpenAsync()
        {
            return Task.Run(() =>
            {
                // Reset closing flag - ensures clean state for reopen after previous close
                _isClosing = false;

                // Fresh cancellation source per session: the previous one was cancelled by the close
                // path, and reusing it would make every send after a reopen fail immediately.
                Volatile.Write(ref _writeCts, new CancellationTokenSource());

                SerialPort? port = null;
                try
                {
                    // Dispose existing port if any
                    if (_serialPort != null)
                    {
                        var oldPort = _serialPort;
                        _serialPort = null;
                        
                        // Every step of this teardown used to be an empty catch. A port that fails to
                        // close is exactly the situation where the user reports "it won't reopen", so
                        // swallowing the reason left nothing to diagnose with. None of it is fatal, so
                        // the errors are logged rather than propagated.
                        try
                        {
                            // Unsubscribe events first
                            try
                            {
                                oldPort.DataReceived -= SerialPort_DataReceived;
                                oldPort.ErrorReceived -= SerialPort_ErrorReceived;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error unsubscribing from the previous instance of port {PortName}", Config.PortName);
                            }

                            // Close if open
                            if (oldPort.IsOpen)
                            {
                                try
                                {
                                    oldPort.Close();
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Error closing the previous instance of port {PortName}", Config.PortName);
                                }
                            }

                            // Dispose - catch known .NET bug
                            try
                            {
                                oldPort.Dispose();
                            }
                            catch (NullReferenceException ex)
                            {
                                // Known .NET SerialPort bug - safe to ignore, but still worth recording.
                                _logger.LogDebug(ex, "Ignored the known NullReferenceException from SerialPort.Dispose() on port {PortName}", Config.PortName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Unexpected error disposing the previous instance of port {PortName}", Config.PortName);
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
                    
                    // CRITICAL: Discard any residual data in the buffers immediately after opening
                    // This prevents "ghost" logs from previous sessions that might be stuck in the driver/OS
                    try
                    {
                        if (port.IsOpen)
                        {
                            port.DiscardInBuffer();
                            port.DiscardOutBuffer();
                            _logger.LogDebug("Discarded residual buffers after opening port {PortName}", Config.PortName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to discard buffers after opening port {PortName}", Config.PortName);
                    }
                    
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
                        catch (Exception cleanupEx)
                        {
                            // Not fatal (the open already failed), but a handle that refuses to close
                            // here is what makes the next open attempt fail with "access denied".
                            _logger.LogWarning(cleanupEx, "Error cleaning up the failed open of port {PortName}", Config.PortName);
                        }
                    }
                    
                    // Ensure field is null on failure
                    _serialPort = null;
                    
                    RaiseError(ex);
                    return false;
                }
            });
        }

        /// <summary>
        /// Stops this session: no more received data is processed and in-flight sends are aborted.
        /// </summary>
        /// <remarks>
        /// Used by the teardown paths, which must stop writers *before* closing the port — closing
        /// first lets a queued write land on a half-disposed <see cref="SerialPort"/>.
        /// </remarks>
        public void BeginShutdown()
        {
            _isClosing = true;
            Interlocked.Exchange(ref _writeCts, null)?.Cancel();
        }

        public Task CloseAsync()
        {
            // Runs on a pool thread: Close() can block on a wedged driver, and callers await this from
            // the UI thread.
            return Task.Run(CloseCore);
        }

        private void CloseCore()
        {
            BeginShutdown();

            if (_serialPort == null)
            {
                _logger.LogDebug("CloseCore called but _serialPort is already null");
                return;
            }

            var port = _serialPort;
            _serialPort = null;

            try
            {
                _logger.LogDebug("Starting close sequence for port {PortName}", Config.PortName);

                // Step 1: Unsubscribe from events FIRST to prevent callbacks during disposal
                try
                {
                    port.DataReceived -= SerialPort_DataReceived;
                    port.ErrorReceived -= SerialPort_ErrorReceived;
                    _logger.LogDebug("Unsubscribed from events for port {PortName}", Config.PortName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error unsubscribing from events for port {PortName}", Config.PortName);
                }

                // Wait a bit for any in-flight event handlers to complete
                Thread.Sleep(50);

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
                    _logger.LogWarning(ex, "Error discarding buffers for port {PortName}", Config.PortName);
                }

                // Step 3: Close. Called directly rather than through Task.Run(...).Wait(timeout):
                // the timeout never bounded anything, because a wait that gives up leaves the Close
                // running on another thread while this one proceeds to Dispose the same port.
                if (port.IsOpen)
                {
                    try
                    {
                        port.Close();
                        _logger.LogInformation("Port {PortName} closed successfully", Config.PortName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error closing port {PortName}; forcing disposal", Config.PortName);
                    }
                }
                else
                {
                    _logger.LogDebug("Port {PortName} was already closed", Config.PortName);
                }

                _logger.LogDebug("Port {PortName} close operations completed", Config.PortName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRITICAL error during port {PortName} close operations", Config.PortName);
            }
            finally
            {
                try
                {
                    port.Dispose();
                    _logger.LogDebug("Port {PortName} disposed successfully", Config.PortName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disposing port {PortName} - continuing cleanup", Config.PortName);
                }

                // (A full GC.Collect + WaitForPendingFinalizers used to run here on every
                // close — tens of milliseconds of blocking that didn't help: the handle is
                // released by Dispose() above. The OS handle-release delay after close is
                // already handled by the availability-retry loop in OpenPortAsync.)

                Statistics.DisconnectedAt = DateTime.Now;
                _logger.LogInformation("Port {PortName} fully closed and resources released", Config.PortName);
            }
        }

        public async Task ReconnectAsync()
        {
            await CloseAsync();
            await Task.Delay(500); // Wait longer for OS to release port resources
            _isClosing = false; // Reset closing flag for reconnection
            await OpenAsync();
        }

        public async Task SendDataAsync(byte[] data)
        {
            // Fail fast on a closed/closing port: taking _writeLock would otherwise succeed and the
            // write would only be rejected after the port had already been disposed underneath us.
            var writeCts = Volatile.Read(ref _writeCts)
                ?? throw new InvalidOperationException($"Port {Config.PortName} is not open");

            await _writeLock.WaitAsync(writeCts.Token);
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    await _serialPort.BaseStream.WriteAsync(data, 0, data.Length, writeCts.Token);
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

        private async void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            // Skip processing if port is being closed
            if (_isClosing)
            {
                return;
            }

            try
            {
                if (_serialPort?.IsOpen == true && _serialPort.BytesToRead > 0)
                {
                    var available = _serialPort.BytesToRead;
                    if (_readScratch.Length < available)
                    {
                        // Grow with headroom so frequent small bursts stop reallocating.
                        var newSize = Math.Max(available, _readScratch.Length * 2);
                        _readScratch = new byte[newSize];
                    }

                    var bytesRead = _serialPort.Read(_readScratch, 0, available);

                    Statistics.ReceivedBytes += bytesRead;
                    Statistics.ReceivedMessages++;

                    // After a garbage-data verdict the port is paused for ~1s. Drain and drop
                    // incoming chunks during that window instead of queueing overlapping
                    // validation work (the per-port decoder is not thread-safe).
                    if (Environment.TickCount64 < Volatile.Read(ref _validationPauseUntilTicks))
                    {
                        return;
                    }

                    // Hand the consumers an exact-sized copy. They may stash it (the validation
                    // service path awaits before forwarding, and the UI path queues it for the
                    // dispatcher), so they must not see the reused scratch buffer.
                    var buffer = new byte[bytesRead];
                    Buffer.BlockCopy(_readScratch, 0, buffer, 0, bytesRead);

                    // 如果有数据验证服务，先验证数据
                    if (_dataValidationService != null)
                    {
                        // Await so the (now synchronous) validation + forwarding finishes before
                        // the next chunk is read — this keeps the per-port Decoder/StringBuilder
                        // touched by a single thread only.
                        await ProcessDataWithValidationAsync(buffer);
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
                // Don't log errors if we're closing
                if (!_isClosing)
                {
                    _logger.LogError(ex, "Error reading data from {PortName}", Config.PortName);
                    RaiseError(ex);
                }
            }
        }

        private async Task ProcessDataWithValidationAsync(byte[] buffer)
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

                        // Arm the ~1s cooldown handled in SerialPort_DataReceived instead of
                        // blocking this thread with Task.Delay(1000).
                        Volatile.Write(ref _validationPauseUntilTicks, Environment.TickCount64 + 1000);
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
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during Dispose for port {PortName}", Config.PortName);
            }
            finally
            {
                // Stop any send that is still waiting on / inside the write lock.
                BeginShutdown();

                // Always give time for OS to fully release the handle, even if disposal threw
                try { Thread.Sleep(100); } catch { }

                // _writeLock and _writeCts are deliberately NOT disposed.
                //
                // Disposing the semaphore here is what produced the ObjectDisposedException on
                // shutdown: a send that was already inside the lock (or about to call WaitAsync) hits
                // a disposed object — either on WaitAsync, or on the Release() in its finally block,
                // which no try/catch there could distinguish from a real bug. Cancelling first does
                // not help, because the loser of that race is always the straggler.
                //
                // Neither object holds an unmanaged resource in this usage (SemaphoreSlim only
                // allocates a wait handle if AvailableWaitHandle is touched, which it never is here;
                // the CancellationTokenSource never uses CancelAfter), so letting the GC collect them
                // together with the PortInstance is correct and, unlike the disposal, cannot throw.
                _ = _writeLock;
            }
        }
    }
}