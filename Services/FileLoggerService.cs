using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 文件日志服务实现 - 优化批量写入性能
/// </summary>
public class FileLoggerService : IFileLoggerService, IDisposable
{
    private readonly ILogger<FileLoggerService> _logger;
    private readonly ConcurrentDictionary<string, LoggerInstance> _loggers = new();
    private readonly string _logDirectory;

    // Serializes the check-and-create in StartLoggingAsync against itself and against
    // StopLoggingAsync. A ConcurrentDictionary alone cannot close that window: `ContainsKey` followed
    // by `_loggers[port] = instance` lets two concurrent starts both build an instance, and the loser
    // was silently dropped — leaking its 64 KB StreamWriter (an open file handle) and its 100 ms
    // flush timer forever, plus leaving a stray half-written log file on disk.
    //
    // Start/Stop are per-port lifecycle calls, not hot paths, so a single service-wide lock is fine.
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public FileLoggerService(ILogger<FileLoggerService> logger)
    {
        _logger = logger;
        
        // Create logs directory in user's Documents folder
        var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _logDirectory = Path.Combine(documentsPath, "SerialPortTool", "Logs");
        
        // Ensure directory exists
        Directory.CreateDirectory(_logDirectory);
        
        _logger.LogInformation("FileLoggerService initialized. Log directory: {LogDirectory}", _logDirectory);
    }

    public async Task StartLoggingAsync(string portName)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_loggers.ContainsKey(portName))
            {
                _logger.LogWarning("Logging already started for port {PortName}", portName);
                return;
            }

            var loggerInstance = new LoggerInstance(portName, _logDirectory, _logger);
            _loggers[portName] = loggerInstance;
            _logger.LogInformation("Started logging for port {PortName}", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting logging for port {PortName}", portName);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopLoggingAsync(string portName)
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_loggers.TryRemove(portName, out var loggerInstance))
            {
                await loggerInstance.DisposeAsync();
                _logger.LogInformation("Stopped logging for port {PortName}", portName);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task WriteLogAsync(string portName, LogEntry entry)
    {
        if (_loggers.TryGetValue(portName, out var loggerInstance))
        {
            await loggerInstance.WriteLogAsync(entry);
        }
    }

    public void WriteLogs(string portName, IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0) return;
        if (_loggers.TryGetValue(portName, out var loggerInstance))
        {
            loggerInstance.WriteLogs(entries);
        }
    }

    public string GetLogFilePath(string portName)
    {
        if (_loggers.TryGetValue(portName, out var loggerInstance))
        {
            return loggerInstance.LogFilePath;
        }
        return string.Empty;
    }

    public bool IsLogging(string portName)
    {
        return _loggers.ContainsKey(portName);
    }

    public void Dispose()
    {
        foreach (var portName in _loggers.Keys.ToList())
        {
            if (!_loggers.TryRemove(portName, out var logger))
            {
                continue;
            }

            try
            {
                // Bounded: DisposeAsync's own awaits are on the writer/timer only, and a wedged disk
                // must not hold up shutdown. A timeout here abandons the flush, which is logged.
                if (!logger.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)))
                {
                    _logger.LogWarning("FileLogger dispose timed out for port {PortName}", portName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing file logger for port {PortName}", portName);
            }
        }

        _loggers.Clear();
        _lifecycleLock.Dispose();
    }

    /// <summary>
    /// 单个串口的日志记录器实例 - 带批量写入优化
    /// </summary>
    private class LoggerInstance : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<LogEntry> _writeQueue = new();
        private readonly Timer _flushTimer;
        private readonly StringBuilder _batchBuffer = new(4096);
        private const int MaxBatchSize = 100;
        private const int FlushIntervalMs = 100;
        private int _queuedCount = 0;
        private volatile bool _disposed;

        // Idempotent teardown: a second DisposeAsync waits for the first instead of running the
        // writer/timer disposal a second time (which threw ObjectDisposedException).
        private int _disposeState;
        private readonly TaskCompletionSource _disposeCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string LogFilePath { get; }

        public LoggerInstance(string portName, string logDirectory, ILogger logger)
        {
            _logger = logger;
            
            // Create log file with timestamp - sanitize port name to prevent path traversal
            var safePortName = string.Concat(portName.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_'));
            if (string.IsNullOrEmpty(safePortName)) safePortName = "unknown";
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{safePortName}_{timestamp}.log";
            LogFilePath = Path.Combine(logDirectory, fileName);

            // Create StreamWriter with UTF-8 encoding and larger buffer
            _writer = new StreamWriter(LogFilePath, append: true, Encoding.UTF8, bufferSize: 65536)
            {
                AutoFlush = false  // 手动刷新以提高性能
            };

            // Write header
            _writer.WriteLine($"=== Serial Port Log ===");
            _writer.WriteLine($"Port: {portName}");
            _writer.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine($"========================");
            _writer.WriteLine();
            _writer.Flush();

            // Start background flush timer
            _flushTimer = new Timer(FlushCallback, null, FlushIntervalMs, FlushIntervalMs);

            _logger.LogInformation("Created log file: {LogFilePath}", LogFilePath);
        }

        public async Task WriteLogAsync(LogEntry entry)
        {
            // 快速入队,避免阻塞
            _writeQueue.Enqueue(entry);
            var count = Interlocked.Increment(ref _queuedCount);

            // 如果累积了足够多的日志,立即触发批量写入
            if (count >= MaxBatchSize)
            {
                await FlushQueueAsync();
            }
        }

        public void WriteLogs(IReadOnlyList<LogEntry> entries)
        {
            if (_disposed) return;

            foreach (var entry in entries)
            {
                _writeQueue.Enqueue(entry);
            }
            Interlocked.Add(ref _queuedCount, entries.Count);
        }

        private void FlushCallback(object? state)
        {
            // 定期刷新队列
            if (_queuedCount > 0)
            {
                _ = FlushQueueAsync();
            }
        }

        private async Task FlushQueueAsync()
        {
            if (_disposed)
            {
                return;
            }

            if (!_writeLock.Wait(0))
            {
                // 如果锁被占用,跳过此次刷新
                return;
            }

            try
            {
                await DrainQueueAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// 取出队列中的日志并写入文件。调用方必须已持有 <see cref="_writeLock"/>。
        /// </summary>
        private async Task DrainQueueAsync()
        {
            _batchBuffer.Clear();
            var processed = 0;

            // 批量从队列中取出日志
            while (processed < MaxBatchSize && _writeQueue.TryDequeue(out var entry))
            {
                var direction = entry.IsReceived ? "RX" : "TX";
                _batchBuffer.Append('[')
                    .Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append("] [")
                    .Append(direction)
                    .Append("] ")
                    .AppendLine(entry.Content);

                processed++;
                Interlocked.Decrement(ref _queuedCount);
            }

            if (processed > 0)
            {
                await _writer.WriteAsync(_batchBuffer.ToString());
                await _writer.FlushAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeState, 1) == 1)
            {
                // Someone else is already tearing this instance down (a lost StartLogging race, or a
                // Stop racing the service Dispose). Wait for that one instead of re-running the
                // disposal against an already-disposed writer and lock.
                await _disposeCompleted.Task;
                return;
            }

            try
            {
                // Stop timer first so no new fire-and-forget flushes get scheduled.
                await _flushTimer.DisposeAsync();

                // Any flush that was already in flight now bails out at its _disposed check
                // instead of racing the writer disposal below (the old code could hit
                // ObjectDisposedException on the StreamWriter and lose the tail of the log).
                _disposed = true;

                // Final drain happens inside the same lock that guards the writer, so it cannot
                // interleave with a straggler flush either.
                await _writeLock.WaitAsync();
                try
                {
                    await DrainQueueAsync();

                    // Write footer
                    await _writer.WriteLineAsync();
                    await _writer.WriteLineAsync($"========================");
                    await _writer.WriteLineAsync($"Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    await _writer.WriteLineAsync($"========================");
                    await _writer.FlushAsync();

                    _writer.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing logger instance");
                }
                finally
                {
                    _writeLock.Dispose();
                }
            }
            finally
            {
                _disposeCompleted.TrySetResult();
            }
        }
    }
}