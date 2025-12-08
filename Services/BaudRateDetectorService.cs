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

    // 用于检测有效数据的正则表达式
    private readonly Regex _validDataRegex = new(@"^[\x20-\x7E\r\n\t]*$", RegexOptions.Compiled);
    
    // 可打印字符比例阈值
    private const double PrintableCharThreshold = 0.7;
    
    // 最小数据量要求
    private const int MinDataBytesForValidation = 10;

    public BaudRateDetectorService(ILogger<BaudRateDetectorService> logger)
    {
        _logger = logger;
    }

    public async Task<List<BaudRateDetectionResult>> DetectOptimalBaudRateAsync(string portName, int testDurationMs = 2000)
    {
        var results = new List<BaudRateDetectionResult>();
        
        _logger.LogInformation("Starting baud rate detection for port {PortName}", portName);

        foreach (var baudRate in _commonBaudRates)
        {
            try
            {
                var result = await TestBaudRateAsync(portName, baudRate, testDurationMs);
                results.Add(result);
                
                _logger.LogDebug("Tested baud rate {BaudRate}: Score={Score}, Valid={Valid}/{Total}",
                    baudRate, result.ConfidenceScore, result.ValidDataCount, result.TotalDataCount);
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
            await Task.Delay(200);
        }

        // 按置信度分数排序
        var sortedResults = results.OrderByDescending(r => r.ConfidenceScore).ToList();
        
        _logger.LogInformation("Baud rate detection completed for port {PortName}. Best match: {BaudRate} with score {Score}",
            portName, sortedResults.FirstOrDefault()?.BaudRate, sortedResults.FirstOrDefault()?.ConfidenceScore);

        return sortedResults;
    }

    public async Task<BaudRateValidationResult> ValidateBaudRateAsync(string portName, int baudRate, int validationDurationMs = 3000)
    {
        _logger.LogInformation("Validating baud rate {BaudRate} on port {PortName}", baudRate, portName);
        
        var result = new BaudRateValidationResult
        {
            SuggestedBaudRates = new List<int>()
        };

        try
        {
            var detectionResult = await TestBaudRateAsync(portName, baudRate, validationDurationMs);
            
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
                
                // 建议其他可能的波特率
                var suggestions = await DetectOptimalBaudRateAsync(portName, 1000);
                result.SuggestedBaudRates = suggestions
                    .Where(r => r.ConfidenceScore > 0.3 && r.BaudRate != baudRate)
                    .Take(3)
                    .Select(r => r.BaudRate)
                    .ToList();
            }

            _logger.LogInformation("Baud rate validation completed for port {PortName}. Valid: {IsValid}, Score: {Score}",
                portName, result.IsValid, result.DataQualityScore);
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

    private async Task<BaudRateDetectionResult> TestBaudRateAsync(string portName, int baudRate, int testDurationMs)
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

        var receivedData = new List<byte>();
        var dataReceivedEvent = new ManualResetEvent(false);

        serialPort.DataReceived += (sender, e) =>
        {
            try
            {
                if (serialPort.IsOpen && serialPort.BytesToRead > 0)
                {
                    var buffer = new byte[serialPort.BytesToRead];
                    var bytesRead = serialPort.Read(buffer, 0, buffer.Length);
                    receivedData.AddRange(buffer.Take(bytesRead));
                    dataReceivedEvent.Set();
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
            var startTime = DateTime.Now;
            while ((DateTime.Now - startTime).TotalMilliseconds < testDurationMs)
            {
                dataReceivedEvent.WaitOne(100);
                dataReceivedEvent.Reset();
            }

            result.TotalDataCount = receivedData.Count;
            
            if (result.TotalDataCount >= MinDataBytesForValidation)
            {
                result.ValidDataCount = CountValidDataBytes(receivedData.ToArray());
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

    private int CountValidDataBytes(byte[] data)
    {
        if (data == null || data.Length == 0)
            return 0;

        try
        {
            var text = Encoding.UTF8.GetString(data);
            
            // 计算可打印字符的数量
            var printableChars = text.Count(c => char.IsControl(c) || (c >= 32 && c <= 126));
            
            // 检查是否有常见的协议模式
            var hasCommonPatterns = Regex.IsMatch(text, @"^(AT|OK|ERROR|READY|[\d\w\s.,!?@#$%^&*()_+=\-\[\]{};:'""<>\\/|`~\r\n\t])*$", RegexOptions.IgnoreCase);
            
            if (hasCommonPatterns)
            {
                printableChars = (int)(printableChars * 1.2); // 给常见模式加分
            }

            return Math.Min(printableChars, data.Length);
        }
        catch
        {
            // 如果UTF-8解码失败，尝试ASCII
            try
            {
                var text = Encoding.ASCII.GetString(data);
                return text.Count(c => char.IsControl(c) || (c >= 32 && c <= 126));
            }
            catch
            {
                return 0;
            }
        }
    }
}