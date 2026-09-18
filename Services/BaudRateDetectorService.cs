using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Services;

/// <summary>
/// 波特率检测服务实现
/// </summary>
public class BaudRateDetectorService : IBaudRateDetectorService
{
    private readonly ILogger<BaudRateDetectorService> _logger;
    
    // 常用波特率列表，按使用频率排序
    private readonly List<int> _commonBaudRates = new()
    {
        9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600,
        1152000, 1500000, 2000000, 2500000, 3000000, 3500000, 4000000,
        6000000, 8000000, 12000000
    };

    // 常见协议模式（CountValidDataBytes 用来给可读数据加分）
    private readonly Regex _commonPatternRegex = new(@"^(AT|OK|ERROR|READY|[\d\w\s.,!?@#$%^&*()_+=\-\[\]{};:'""<>\\/|`~\r\n\t])*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    
    // 可打印字符比例阈值
    private const double PrintableCharThreshold = 0.7;
    
    // 最小数据量要求
    private const int MinDataBytesForValidation = 10;

    public BaudRateDetectorService(ILogger<BaudRateDetectorService> logger)
    {
        _logger = logger;
    }

    public async Task<List<BaudRateDetectionResult>> DetectOptimalBaudRateAsync(
        string portName,
        int testDurationMs = 2000,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BaudRateDetectionResult>();
        
        _logger.LogInformation("Starting baud rate detection for port {PortName}", portName);

        foreach (var baudRate in _commonBaudRates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await TestBaudRateAsync(portName, baudRate, testDurationMs, cancellationToken);
                results.Add(result);
                
                _logger.LogDebug("Tested baud rate {BaudRate}: Score={Score}, Valid={Valid}/{Total}",
                    baudRate, result.ConfidenceScore, result.ValidDataCount, result.TotalDataCount);
            }
            catch (OperationCanceledException)
            {
                // Never swallow cancellation into a "score 0" result — the caller is shutting down and
                // must be able to tell a cancelled scan apart from a genuinely bad baud rate.
                _logger.LogInformation("Baud rate detection cancelled for port {PortName}", portName);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to test baud rate {BaudRate} on port {PortName}", baudRate, portName);
                
                results.Add(new BaudRateDetectionResult
                {
                    BaudRate = baudRate,
                    ConfidenceScore = 0,
                    ValidDataCount = 0,
                    TotalDataCount = 0,
                    Reason = ex.Message
                });
            }
            
            // 在测试之间添加短暂延迟，确保串口资源释放
            await Task.Delay(200, cancellationToken);
        }

        // 按置信度分数排序
        var sortedResults = results.OrderByDescending(r => r.ConfidenceScore).ToList();
        
        _logger.LogInformation("Baud rate detection completed for port {PortName}. Best match: {BaudRate} with score {Score}",
            portName, sortedResults.FirstOrDefault()?.BaudRate, sortedResults.FirstOrDefault()?.ConfidenceScore);

        return sortedResults;
    }

    public async Task<BaudRateValidationResult> ValidateBaudRateAsync(
        string portName,
        int baudRate,
        int validationDurationMs = 3000,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Validating baud rate {BaudRate} on port {PortName}", baudRate, portName);
        
        var result = new BaudRateValidationResult
        {
            SuggestedBaudRates = new List<int>()
        };

        try
        {
            var detectionResult = await TestBaudRateAsync(portName, baudRate, validationDurationMs, cancellationToken);
            
            result.IsValid = detectionResult.ConfidenceScore > 0.5;
            result.DataQualityScore = detectionResult.ConfidenceScore;
            result.ValidDataCount = detectionResult.ValidDataCount;
            result.InvalidDataCount = detectionResult.TotalDataCount - detectionResult.ValidDataCount;

            if (result.IsValid)
            {
                result.Recommendation = $"波特率 {baudRate} 工作正常，数据质量评分: {result.DataQualityScore:F2}";
            }
            else
            {
                result.Recommendation = $"波特率 {baudRate} 可能不正确，数据质量评分: {result.DataQualityScore:F2}";
                
                // 建议其他可能的波特率。这一轮是完整的 18 个波特率扫描，必须可取消 —— 否则一次
                // 失败的校验会把调用方拖住额外约 20 秒，关窗也退不出去。
                var suggestions = await DetectOptimalBaudRateAsync(portName, 1000, cancellationToken);
                result.SuggestedBaudRates = suggestions
                    .Where(r => r.ConfidenceScore > 0.3 && r.BaudRate != baudRate)
                    .Take(3)
                    .Select(r => r.BaudRate)
                    .ToList();
            }

            _logger.LogInformation("Baud rate validation completed for port {PortName}. Valid: {IsValid}, Score: {Score}",
                portName, result.IsValid, result.DataQualityScore);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Baud rate validation cancelled for port {PortName}", portName);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating baud rate {BaudRate} on port {PortName}", baudRate, portName);
            result.IsValid = false;
            result.Recommendation = $"验证失败: {ex.Message}";
        }

        return result;
    }

    public List<int> GetCommonBaudRates()
    {
        return new List<int>(_commonBaudRates);
    }

    private async Task<BaudRateDetectionResult> TestBaudRateAsync(
        string portName,
        int baudRate,
        int testDurationMs,
        CancellationToken cancellationToken)
    {
        var result = new BaudRateDetectionResult
        {
            BaudRate = baudRate
        };

        using var serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None
        };

        // The DataReceived callback runs on the serial driver's worker thread; the loop below reads
        // the accumulated bytes on this thread. The previous bare List<byte> was written and read
        // concurrently — a torn/grown-mid-enumeration List is how that ends in an IndexOutOfRange or
        // a silently short count.
        var receivedData = new List<byte>();
        var receivedDataLock = new object();

        // SemaphoreSlim + await instead of ManualResetEvent + WaitOne(100): the blocking wait parked
        // a thread-pool thread for the whole scan (~40 s across the rate list), could not be
        // cancelled, and the event was never disposed.
        using var dataReceivedSignal = new SemaphoreSlim(0, 1);

        serialPort.DataReceived += (sender, e) =>
        {
            try
            {
                if (serialPort.IsOpen && serialPort.BytesToRead > 0)
                {
                    var buffer = new byte[serialPort.BytesToRead];
                    var bytesRead = serialPort.Read(buffer, 0, buffer.Length);
                    lock (receivedDataLock)
                    {
                        receivedData.AddRange(buffer.Take(bytesRead));
                    }

                    TrySignal(dataReceivedSignal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error reading data during baud rate test");
            }
        };

        try
        {
            serialPort.Open();
            
            // 等待数据接收
            var deadline = Environment.TickCount64 + testDurationMs;
            while (true)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    break;
                }

                // Returns false on timeout; either way the loop re-checks the deadline. Cancellation
                // throws out of here and is closed down by the finally below.
                await dataReceivedSignal.WaitAsync(
                    (int)Math.Min(100, remaining), cancellationToken);
            }

            int totalDataCount;
            byte[] snapshot;
            lock (receivedDataLock)
            {
                totalDataCount = receivedData.Count;
                snapshot = receivedData.ToArray();
            }

            result.TotalDataCount = totalDataCount;
            
            if (result.TotalDataCount >= MinDataBytesForValidation)
            {
                result.ValidDataCount = CountValidDataBytes(snapshot);
                result.ConfidenceScore = (double)result.ValidDataCount / result.TotalDataCount;
                
                if (result.ConfidenceScore >= PrintableCharThreshold)
                {
                    result.Reason = "数据质量良好，大部分字符为可打印字符";
                }
                else if (result.ConfidenceScore >= 0.4)
                {
                    result.Reason = "数据质量一般，部分字符为可打印字符";
                }
                else
                {
                    result.Reason = "数据质量较差，大部分字符为乱码";
                }
            }
            else
            {
                result.ConfidenceScore = 0;
                result.Reason = "接收数据量不足，无法判断";
            }
        }
        finally
        {
            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }
        }

        return result;
    }

    /// <summary>
    /// Signals <paramref name="signal"/>, tolerating an already-signalled semaphore.
    /// </summary>
    /// <remarks>
    /// The semaphore is capped at 1 so bursts of received chunks do not queue up counts nobody reads;
    /// <see cref="SemaphoreSlim.Release()"/> throws when that cap is already reached, which is a
    /// completely normal outcome here (data keeps arriving while the loop is between waits).
    /// </remarks>
    private static void TrySignal(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled — nothing to do.
        }
    }

    private int CountValidDataBytes(byte[] data)
    {
        if (data == null || data.Length == 0)
            return 0;

        // Encoding.UTF8.GetString uses replacement fallback and never throws.
        var text = Encoding.UTF8.GetString(data);

        // 可打印字符数量。\r\n\t 是合法的文本控制字符，不算乱码。
        var printableChars = text.Count(c => (c >= 32 && c <= 126) || c == '\r' || c == '\n' || c == '\t');

        // 检查是否有常见的协议模式（复用缓存的正则，避免每次调用重新解析）
        var hasCommonPatterns = _commonPatternRegex.IsMatch(text);

        if (hasCommonPatterns)
        {
            printableChars = (int)(printableChars * 1.2); // 给常见模式加分
        }

        return Math.Min(printableChars, data.Length);
    }
}