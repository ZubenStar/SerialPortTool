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

    // One entry per port name validated in this session. Deliberately never removed: a port can be
    // closed and reopened, and ResetValidationState zeroes the state rather than deleting it. The cost
    // is a few counters per name ever seen, not per chunk.
    private readonly ConcurrentDictionary<string, PortValidationState> _portStates = new();
    
    // 验证配置常量
    private const double MinQualityScore = 0.3;
    private const double GoodQualityScore = 0.7;
    private const int MaxConsecutiveInvalidPackets = 10;
    private const int MinPacketsForTrendAnalysis = 20;
    private const double GarbageDataThreshold = 0.8;

    // Below this printable-character ratio a chunk is treated as binary / non-ASCII text and is
    // passed through untouched instead of being run through the lossy CleanData path.
    private const double CleanableTextRatio = 0.7;
    
    // 正则表达式模式
    private readonly Regex _commonProtocolRegex = new(@"^(AT|OK|ERROR|READY|[\d\w\s.,!?@#$%^&*()_+=\-\[\]{};:'""<>\\/|`~\r\n\t])*$", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private readonly Regex _hexPatternRegex = new(@"^[0-9A-Fa-f\s\r\n]*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public DataValidationService(ILogger<DataValidationService> logger)
    {
        _logger = logger;
    }

    public Task<DataValidationResult> ValidateDataAsync(byte[] data, string portName, Encoding encoding)
    {
        // Runs synchronously on the caller's thread. The DataReceived path delivers chunks
        // per-port in order on the serial driver's worker thread; a Task.Run here hopped to
        // the pool on every chunk, which both reordered deliveries under load and thrashed
        // the thread pool at high baud rates. The per-port PortValidationState is only ever
        // touched by that port's worker thread, so the statistics below stay race-free.
        return Task.FromResult(ValidateDataCore(data, portName, encoding));
    }

    private DataValidationResult ValidateDataCore(byte[] data, string portName, Encoding encoding)
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

            // 计算数据质量评分。解码用该端口自己的编码：按 UTF-8 解一个 GB18030 字节流只会得到替换
            // 字符，评分随即跌到「乱码」区间，数据会被丢弃或误触发波特率检测。
            var text = encoding.GetString(data);
            var printableRatio = ComputePrintableRatio(text);
            var qualityScore = CalculateDataQualityScore(data, text, printableRatio);
            result.QualityScore = qualityScore;

            // 更新端口统计
            portState.RecordSample(qualityScore);

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

                if (printableRatio >= CleanableTextRatio)
                {
                    result.SuggestedAction = ValidationAction.CleanAndProcess;
                    result.ProcessedData = CleanData(data, encoding);
                    result.Message = "数据质量一般，已清理";
                }
                else
                {
                    // 二进制 / 非 ASCII 文本：CleanData 会剥离字节从而破坏载荷，改为原样透传。
                    result.SuggestedAction = ValidationAction.Normal;
                    result.Message = "数据质量一般，按原始字节处理";
                }
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
    }

    public Task<bool> ShouldTriggerBaudRateDetectionAsync(string portName)
    {
        // Pure in-memory check — see ValidateDataAsync for why this must not Task.Run. This runs on
        // every received chunk, so the check itself lives on PortValidationState under the same lock
        // the sample recording takes, and returns a bool instead of materialising a statistics
        // object (that allocation would land on the per-port read thread at chunk rate).
        if (!_portStates.TryGetValue(portName, out var portState))
            return Task.FromResult(false);

        return Task.FromResult(portState.ShouldTriggerBaudRateDetection());
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

        // Under the state's own lock: reading the fields one by one let a sample land between two
        // reads and produce a combination that never existed.
        return portState.Snapshot();
    }

    /// <summary>
    /// 文本似然度：解码后有多少字符读起来是文本而不是噪声。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts everything except control characters (CR/LF/TAB excepted) and U+FFFD, the replacement
    /// character a decoder emits for bytes that are not valid in the current encoding.
    /// </para>
    /// <para>
    /// The previous definition counted only ASCII 32..126, which made correctly decoded CJK text look
    /// like binary data: a GB18030 or UTF-8 Chinese log scored near zero and therefore landed in the
    /// <see cref="ValidationAction.Discard"/> / <see cref="ValidationAction.TriggerBaudRateDetection"/>
    /// band. Binary payloads still score near zero, because they decode to replacement characters — the
    /// detection is not weakened by this, it is corrected.
    /// </para>
    /// <para>
    /// Span loop instead of text.Count(lambda): the LINQ path boxes string's enumerator and pays a
    /// delegate call per character, on the per-port read thread, for every received chunk.
    /// </para>
    /// </remarks>
    private static double ComputePrintableRatio(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0;

        var printableChars = 0;
        var span = text.AsSpan();
        for (var i = 0; i < span.Length; i++)
        {
            var c = span[i];
            if (c == '\uFFFD')
            {
                continue;
            }

            // \r\n\t 是合法的文本控制字符，不算乱码；其余控制字符算噪声。
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
            {
                continue;
            }

            printableChars++;
        }

        return (double)printableChars / text.Length;
    }

    private double CalculateDataQualityScore(byte[] data, string text, double printableRatio)
    {
        if (data == null || data.Length == 0)
            return 0.0;

        var score = 0.0;

        // 1. 可打印字符比例 (权重: 0.4)
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

    private double AnalyzeCharacterDistribution(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0.0;

        // The distinct-char count used to be text.Distinct().Count(), which allocates a HashSet, its
        // buckets and an enumerator on every received chunk — i.e. on the per-port read thread under
        // full throughput. A 256-bit stack bitmap answers the same question with no allocation; the
        // count is capped at >= 50 because that is all the score below ever needs.
        Span<ulong> seen = stackalloc ulong[4];
        var uniqueChars = 0;
        var chars = text.AsSpan();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var bitIndex = c > 255 ? 255 : c;
            ref var word = ref seen[bitIndex >> 6];
            var mask = 1UL << (bitIndex & 63);
            if ((word & mask) == 0)
            {
                word |= mask;
                if (++uniqueChars >= 50)
                {
                    break;
                }
            }
        }

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

    /// <summary>
    /// 去掉控制字符后重新编码，其余内容原样保留。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only control characters are dropped now. The previous implementation kept a character only if it
    /// was in 32..126 (plus CR/LF/TAB), which silently deleted every non-ASCII character: the line
    /// <c>AT+READY 温度</c> reached the log as <c>AT+READY</c>. That is not a cleaning rule but data loss,
    /// and it fired exactly where mixed ASCII+CJK traffic lands (middle quality band, printable ratio at
    /// or above <see cref="CleanableTextRatio"/>). U+FFFD is deliberately kept as well — a debugging tool
    /// should show that the bytes did not decode rather than hide the evidence.
    /// </para>
    /// <para>
    /// The chunk is decoded and re-encoded with the port's own encoding, so a GB18030 payload is not
    /// round-tripped through UTF-8 on the way.
    /// </para>
    /// </remarks>
    private static byte[] CleanData(byte[] data, Encoding encoding)
    {
        var text = encoding.GetString(data);

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsControl(c) || c == '\r' || c == '\n' || c == '\t')
            {
                builder.Append(c);
            }
        }

        var cleanedText = builder.ToString();

        // 限制长度以防止内存问题
        if (cleanedText.Length > 2048)
        {
            cleanedText = cleanedText.Substring(0, 2048);
        }

        return encoding.GetBytes(cleanedText);
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
            // data.Count(lambda) boxed byte[]'s enumerator and called a delegate per byte; this runs on
            // the per-port read thread for every chunk. Early-exit as soon as the 80 % verdict is settled.
            var firstByte = data[0];
            var sameByteCount = 0;
            var threshold = data.Length * 0.8;
            for (var i = 0; i < data.Length; i++)
            {
                if (data[i] == firstByte && ++sameByteCount > threshold)
                {
                    return true;
                }
            }
        }

        return false;
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
        private readonly object _trendLock = new();

        /// <summary>
        /// 记录一次验证样本并更新统计与趋势。
        /// </summary>
        /// <remarks>
        /// The counters live under the same lock as <see cref="Reset"/> and
        /// <see cref="ShouldTriggerBaudRateDetection"/>. Without that, the read thread landing a
        /// sample while <c>Reset</c> (UI thread, on port close) was zeroing the counters divided by
        /// a zeroed packet count — the average became NaN/∞ and every trend reading built on it was
        /// garbage. The per-port read thread is the only producer, so the lock is uncontended in
        /// the steady state.
        /// </remarks>
        public void RecordSample(double qualityScore)
        {
            lock (_trendLock)
            {
                TotalPackets++;
                LastUpdateTime = DateTime.Now;

                if (qualityScore >= MinQualityScore)
                {
                    ValidPackets++;
                    ConsecutiveInvalidPackets = 0;
                }
                else
                {
                    InvalidPackets++;
                    ConsecutiveInvalidPackets++;
                }

                // 计算平均质量评分
                AverageQualityScore = (AverageQualityScore * (TotalPackets - 1) + qualityScore) / TotalPackets;

                // 更新趋势分析
                if (TotalPackets >= MinPacketsForTrendAnalysis)
                {
                    UpdateTrendLocked();
                }
            }
        }

        /// <summary>
        /// 是否需要触发波特率重新检测（锁内判断）。
        /// </summary>
        /// <remarks>
        /// Called for every received chunk, so it returns a bool rather than a statistics object: the
        /// object would be an allocation per chunk on the read thread.
        /// </remarks>
        public bool ShouldTriggerBaudRateDetection()
        {
            lock (_trendLock)
            {
                // 检查连续无效数据包数量
                if (ConsecutiveInvalidPackets >= MaxConsecutiveInvalidPackets)
                {
                    return true;
                }

                // 检查平均质量评分
                if (TotalPackets >= MinPacketsForTrendAnalysis && AverageQualityScore < MinQualityScore)
                {
                    return true;
                }

                // 检查数据质量趋势
                return Trend == DataQualityTrend.Deteriorating && AverageQualityScore < MinQualityScore * 1.5;
            }
        }

        /// <summary>锁内一次性读出全部统计字段，供统计视图使用（低频）。</summary>
        public DataQualityStatistics Snapshot()
        {
            lock (_trendLock)
            {
                return new DataQualityStatistics
                {
                    TotalPackets = TotalPackets,
                    ValidPackets = ValidPackets,
                    InvalidPackets = InvalidPackets,
                    AverageQualityScore = AverageQualityScore,
                    ConsecutiveInvalidPackets = ConsecutiveInvalidPackets,
                    LastUpdateTime = LastUpdateTime,
                    Trend = Trend
                };
            }
        }

        public void Reset()
        {
            lock (_trendLock)
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
        }

        /// <summary>调用者必须已持有 <see cref="_trendLock"/>。</summary>
        private void UpdateTrendLocked()
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