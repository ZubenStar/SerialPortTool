using System;
using System.Collections.Concurrent;
using System.IO;
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

    public Task StartLoggingAsync(string portName)
    {
        if (_loggers.ContainsKey(portName))
        {
            _logger.LogWarning("Logging already started for port {PortName}", portName);
            return Task.CompletedTask;
        }

        try
        {
            var loggerInstance = new LoggerInstance(portName, _logDirectory, _logger);
            _loggers[portName] = loggerInstance;
            _logger.LogInformation("Started logging for port {PortName}", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting logging for port {PortName}", portName);
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopLoggingAsync(string portName)
    {
        if (_loggers.TryRemove(portName, out var loggerInstance))
        {
            await loggerInstance.DisposeAsync();
            _logger.LogInformation("Stopped logging for port {PortName}", portName);
        }
    }

    public async Task WriteLogAsync(string portName, LogEntry entry)
    {
        if (_loggers.TryGetValue(portName, out var loggerInstance))
        {
            await loggerInstance.WriteLogAsync(entry);
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
        foreach (var logger in _loggers.Values)
        {
            logger.DisposeAsync().AsTask().Wait();
        }
        _loggers.Clear();
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

        public string LogFilePath { get; }

        public LoggerInstance(string portName, string logDirectory, ILogger logger)
        {
            _logger = logger;
            
            // Create log file with timestamp
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"{portName}_{timestamp}.log";
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
            if (!_writeLock.Wait(0))
            {
                // 如果锁被占用,跳过此次刷新
                return;
            }

            try
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error flushing log queue");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Stop timer
            await _flushTimer.DisposeAsync();

            // Flush remaining logs
            await FlushQueueAsync();

            await _writeLock.WaitAsync();
            try
            {
                // Write footer
                await _writer.WriteLineAsync();
                await _writer.WriteLineAsync($"========================");
                await _writer.WriteLineAsync($"Stopped: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                await _writer.WriteLineAsync($"========================");
                await _writer.FlushAsync();

                _writer.Dispose();
                _writeLock.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing logger instance");
            }
        }
    }
}