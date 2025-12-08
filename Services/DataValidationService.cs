using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Services;

/// <summary>
/// 数据验证服务实现
/// </summary>
public class DataValidationService : IDataValidationService
{
    private readonly ILogger<DataValidationService> _logger;
    private readonly ConcurrentDictionary<string, PortValidationState> _portStates = new();
    
    // 验证配置常量
    private const double MinQualityScore = 0.3;
    private const double GoodQualityScore = 0.7;
    private const int MaxConsecutiveInvalidPackets = 10;
    private const int MinPacketsForTrendAnalysis = 20;
    private const double GarbageDataThreshold = 0.8;
    
    // 正则表达式模式
    private readonly Regex _printableCharRegex = new(@"^[\x20-\x7E\r\n\t]*$", RegexOptions.Compiled);
    private readonly Regex _commonProtocolRegex = new(@"^(AT|OK|ERROR|READY|[\d\w\s.,!?@#$%^&*()_+=\-\[\]{};:'""<>\\/|`~\r\n\t])*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly Regex _hexPatternRegex = new(@"^[0-9A-Fa-f\s\r\n]*$", RegexOptions.Compiled);

    public DataValidationService(ILogger<DataValidationService> logger)
    {
        _logger = logger;
    }

    public async Task<DataValidationResult> ValidateDataAsync(byte[] data, string portName)
    {
        return await Task.Run(() =>
        {
            var result = new DataValidationResult
            {
                IsValid = true,
                QualityScore = 1.0,
                SuggestedAction = ValidationAction.Normal,
                ProcessedData = data,
                ShouldDiscard = false
            };

            if (data == null || data.Length == 0)
            {
                result.IsValid = false;
                result.ShouldDiscard = true;
                result.Message = "数据为空";
                return result;
            }

            try
            {
                // 获取或创建端口状态
                var portState = _portStates.GetOrAdd(portName, _ => new PortValidationState());

                // 计算数据质量评分
                var qualityScore = CalculateDataQualityScore(data);
                result.QualityScore = qualityScore;

                // 更新端口统计
                UpdatePortStatistics(portState, qualityScore);

                // 根据质量评分决定处理方式
                if (qualityScore >= GoodQualityScore)
                {
                    result.IsValid = true;
                    result.SuggestedAction = ValidationAction.Normal;
                    result.Message = "数据质量良好";
                }
                else if (qualityScore >= MinQualityScore)
                {
                    result.IsValid = true;
                    result.SuggestedAction = ValidationAction.CleanAndProcess;
                    result.ProcessedData = CleanData(data);
                    result.Message = "数据质量一般，已清理";
                }
                else
                {
                    result.IsValid = false;
                    result.SuggestedAction = ValidationAction.Discard;
                    result.ShouldDiscard = true;
                    result.Message = "数据质量差，建议丢弃";

                    // 检查是否需要触发波特率重新检测
                    if (portState.ConsecutiveInvalidPackets >= MaxConsecutiveInvalidPackets)
                    {
                        result.SuggestedAction = ValidationAction.TriggerBaudRateDetection;
                        result.Message = "连续收到大量无效数据，建议重新检测波特率";
                    }
                }

                // 检查是否为垃圾数据（可能导致死机）
                if (IsGarbageData(data, qualityScore))
                {
                    result.SuggestedAction = ValidationAction.PauseProcessing;
                    result.ShouldDiscard = true;
                    result.Message = "检测到可能的垃圾数据，暂停处理以防止死机";
                    _logger.LogWarning("Garbage data detected on port {PortName}, pausing processing", portName);
                }

                _logger.LogTrace("Data validation for port {PortName}: Score={Score}, Action={Action}, Message={Message}",
                    portName, qualityScore, result.SuggestedAction, result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating data for port {PortName}", portName);
                result.IsValid = false;
                result.SuggestedAction = ValidationAction.Discard;
                result.ShouldDiscard = true;
                result.Message = $"验证过程出错: {ex.Message}";
            }

            return result;
        });
    }

    public async Task<bool> ShouldTriggerBaudRateDetectionAsync(string portName)
    {
        return await Task.Run(() =>
        {
            if (!_portStates.TryGetValue(portName, out var portState))
                return false;

            // 检查连续无效数据包数量
            if (portState.ConsecutiveInvalidPackets >= MaxConsecutiveInvalidPackets)
                return true;

            // 检查平均质量评分
            if (portState.TotalPackets >= MinPacketsForTrendAnalysis && 
                portState.AverageQualityScore < MinQualityScore)
                return true;

            // 检查数据质量趋势
            if (portState.Trend == DataQualityTrend.Deteriorating && 
                portState.AverageQualityScore < MinQualityScore * 1.5)
                return true;

            return false;
        });
    }

    public void ResetValidationState(string portName)
    {
        if (_portStates.TryGetValue(portName, out var portState))
        {
            portState.Reset();
            _logger.LogInformation("Reset validation state for port {PortName}", portName);
        }
    }

    public DataQualityStatistics GetDataQualityStatistics(string portName)
    {
        if (!_portStates.TryGetValue(portName, out var portState))
        {
            return new DataQualityStatistics
            {
                LastUpdateTime = DateTime.Now,
                Trend = DataQualityTrend.Stable
            };
        }

        return new DataQualityStatistics
        {
            TotalPackets = portState.TotalPackets,
            ValidPackets = portState.ValidPackets,
            InvalidPackets = portState.InvalidPackets,
            AverageQualityScore = portState.AverageQualityScore,
            ConsecutiveInvalidPackets = portState.ConsecutiveInvalidPackets,
            LastUpdateTime = portState.LastUpdateTime,
            Trend = portState.Trend
        };
    }

    private double CalculateDataQualityScore(byte[] data)
    {
        if (data == null || data.Length == 0)
            return 0.0;

        try
        {
            var text = Encoding.UTF8.GetString(data);
            var score = 0.0;

            // 1. 可打印字符比例 (权重: 0.4)
            var printableChars = text.Count(c => char.IsControl(c) || (c >= 32 && c <= 126));
            var printableRatio = (double)printableChars / text.Length;
            score += printableRatio * 0.4;

            // 2. 常见协议模式匹配 (权重: 0.3)
            if (_commonProtocolRegex.IsMatch(text))
            {
                score += 0.3;
            }
            else if (_hexPatternRegex.IsMatch(text) && text.Length > 10)
            {
                score += 0.2; // 十六进制数据给部分分数
            }

            // 3. 字符分布分析 (权重: 0.2)
            var charDistribution = AnalyzeCharacterDistribution(text);
            score += charDistribution * 0.2;

            // 4. 数据长度合理性 (权重: 0.1)
            var lengthScore = CalculateLengthScore(data.Length);
            score += lengthScore * 0.1;

            return Math.Min(1.0, Math.Max(0.0, score));
        }
        catch
        {
            // UTF-8 解码失败，尝试 ASCII
            try
            {
                var text = Encoding.ASCII.GetString(data);
                var printableChars = text.Count(c => char.IsControl(c) || (c >= 32 && c <= 126));
                return (double)printableChars / text.Length * 0.5; // ASCII 解码失败给较低分数
            }
            catch
            {
                return 0.0; // 完全无法解码
            }
        }
    }

    private double AnalyzeCharacterDistribution(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0;

        var uniqueChars = text.Distinct().Count();
        var totalChars = text.Length;
        
        // 字符多样性分析
        var diversityScore = Math.Min(1.0, (double)uniqueChars / Math.Min(50, totalChars));
        
        // 检查是否有重复模式（可能是乱码）
        var hasRepeatingPattern = HasRepeatingPattern(text);
        if (hasRepeatingPattern)
            diversityScore *= 0.5;

        return diversityScore;
    }

    private bool HasRepeatingPattern(string text)
    {
        if (text.Length < 10)
            return false;

        // 检查是否有大量重复字符
        var maxConsecutiveSame = 0;
        var currentConsecutive = 1;
        
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == text[i - 1])
            {
                currentConsecutive++;
                maxConsecutiveSame = Math.Max(maxConsecutiveSame, currentConsecutive);
            }
            else
            {
                currentConsecutive = 1;
            }
        }

        return maxConsecutiveSame > text.Length * 0.3; // 如果30%以上是同一字符
    }

    private double CalculateLengthScore(int length)
    {
        // 合理的数据长度范围
        if (length == 0) return 0.0;
        if (length <= 4) return 0.3; // 太短
        if (length <= 1024) return 1.0; // 合理长度
        if (length <= 4096) return 0.8; // 较长但可接受
        return 0.5; // 太长，可能是垃圾数据
    }

    private byte[] CleanData(byte[] data)
    {
        try
        {
            var text = Encoding.UTF8.GetString(data);
            
            // 移除不可打印字符（保留控制字符）
            var cleanedText = new string(text.Where(c => char.IsControl(c) || (c >= 32 && c <= 126)).ToArray());
            
            // 限制长度以防止内存问题
            if (cleanedText.Length > 2048)
            {
                cleanedText = cleanedText.Substring(0, 2048);
            }

            return Encoding.UTF8.GetBytes(cleanedText);
        }
        catch
        {
            // 清理失败，返回原始数据的前1KB
            var maxLength = Math.Min(data.Length, 1024);
            var result = new byte[maxLength];
            Array.Copy(data, result, maxLength);
            return result;
        }
    }

    private bool IsGarbageData(byte[] data, double qualityScore)
    {
        // 检查是否为垃圾数据的条件
        if (qualityScore < 0.1) return true;
        if (data.Length > 8192 && qualityScore < 0.2) return true; // 大量低质量数据
        if (data.Length > 16384) return true; // 超大数据包
        
        // 检查是否包含大量相同字节
        if (data.Length > 100)
        {
            var firstByte = data[0];
            var sameByteCount = data.Count(b => b == firstByte);
            if (sameByteCount > data.Length * 0.8) return true;
        }

        return false;
    }

    private void UpdatePortStatistics(PortValidationState portState, double qualityScore)
    {
        portState.TotalPackets++;
        portState.LastUpdateTime = DateTime.Now;

        if (qualityScore >= MinQualityScore)
        {
            portState.ValidPackets++;
            portState.ConsecutiveInvalidPackets = 0;
        }
        else
        {
            portState.InvalidPackets++;
            portState.ConsecutiveInvalidPackets++;
        }

        // 计算平均质量评分
        portState.AverageQualityScore = (portState.AverageQualityScore * (portState.TotalPackets - 1) + qualityScore) / portState.TotalPackets;

        // 更新趋势分析
        if (portState.TotalPackets >= MinPacketsForTrendAnalysis)
        {
            portState.UpdateTrend();
        }
    }

    /// <summary>
    /// 端口验证状态
    /// </summary>
    private class PortValidationState
    {
        public int TotalPackets { get; set; }
        public int ValidPackets { get; set; }
        public int InvalidPackets { get; set; }
        public double AverageQualityScore { get; set; }
        public int ConsecutiveInvalidPackets { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public DataQualityTrend Trend { get; set; } = DataQualityTrend.Stable;
        
        private readonly Queue<double> _recentScores = new();

        public void Reset()
        {
            TotalPackets = 0;
            ValidPackets = 0;
            InvalidPackets = 0;
            AverageQualityScore = 0;
            ConsecutiveInvalidPackets = 0;
            LastUpdateTime = DateTime.Now;
            Trend = DataQualityTrend.Stable;
            _recentScores.Clear();
        }

        public void UpdateTrend()
        {
            _recentScores.Enqueue(AverageQualityScore);
            if (_recentScores.Count > 10)
            {
                _recentScores.Dequeue();
            }

            if (_recentScores.Count >= 5)
            {
                var recentAverage = _recentScores.Average();
                var olderAverage = _recentScores.Take(_recentScores.Count / 2).Average();

                if (recentAverage > olderAverage * 1.1)
                {
                    Trend = DataQualityTrend.Improving;
                }
                else if (recentAverage < olderAverage * 0.9)
                {
                    Trend = DataQualityTrend.Deteriorating;
                }
                else
                {
                    Trend = DataQualityTrend.Stable;
                }
            }
        }
    }
}