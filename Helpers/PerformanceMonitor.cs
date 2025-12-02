using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Helpers;

/// <summary>
/// 性能监控工具 - 用于追踪和记录关键操作的性能指标
/// </summary>
public class PerformanceMonitor
{
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, PerformanceMetrics> _metrics = new();

    public PerformanceMonitor(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 开始测量操作性能
    /// </summary>
    public IDisposable Measure(string operationName)
    {
        return new PerformanceMeasurement(this, operationName);
    }

    /// <summary>
    /// 记录操作耗时
    /// </summary>
    internal void RecordMetric(string operationName, long elapsedMs)
    {
        var metrics = _metrics.GetOrAdd(operationName, _ => new PerformanceMetrics());
        metrics.Record(elapsedMs);

        // 如果操作耗时超过阈值,记录警告
        if (elapsedMs > 100)
        {
            _logger?.LogWarning("Slow operation detected: {Operation} took {ElapsedMs}ms", operationName, elapsedMs);
        }
    }

    /// <summary>
    /// 获取操作的性能统计
    /// </summary>
    public PerformanceMetrics? GetMetrics(string operationName)
    {
        return _metrics.TryGetValue(operationName, out var metrics) ? metrics : null;
    }

    /// <summary>
    /// 记录性能报告
    /// </summary>
    public void LogReport()
    {
        if (_logger == null) return;

        _logger.LogInformation("=== Performance Report ===");
        foreach (var kvp in _metrics)
        {
            var m = kvp.Value;
            _logger.LogInformation(
                "{Operation}: Count={Count}, Avg={Avg}ms, Min={Min}ms, Max={Max}ms",
                kvp.Key, m.Count, m.AverageMs, m.MinMs, m.MaxMs);
        }
    }

    private class PerformanceMeasurement : IDisposable
    {
        private readonly PerformanceMonitor _monitor;
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;

        public PerformanceMeasurement(PerformanceMonitor monitor, string operationName)
        {
            _monitor = monitor;
            _operationName = operationName;
            _stopwatch = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _stopwatch.Stop();
            _monitor.RecordMetric(_operationName, _stopwatch.ElapsedMilliseconds);
        }
    }
}

/// <summary>
/// 性能指标统计
/// </summary>
public class PerformanceMetrics
{
    private long _totalMs;
    private long _count;
    private long _minMs = long.MaxValue;
    private long _maxMs;
    private readonly object _lock = new();

    public long Count => _count;
    public long TotalMs => _totalMs;
    public long AverageMs => _count > 0 ? _totalMs / _count : 0;
    public long MinMs => _minMs == long.MaxValue ? 0 : _minMs;
    public long MaxMs => _maxMs;

    internal void Record(long elapsedMs)
    {
        lock (_lock)
        {
            _totalMs += elapsedMs;
            _count++;
            if (elapsedMs < _minMs) _minMs = elapsedMs;
            if (elapsedMs > _maxMs) _maxMs = elapsedMs;
        }
    }

    public override string ToString()
    {
        return $"Count: {Count}, Avg: {AverageMs}ms, Min: {MinMs}ms, Max: {MaxMs}ms";
    }
}