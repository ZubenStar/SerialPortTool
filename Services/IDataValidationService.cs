using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 数据验证服务接口
/// </summary>
public interface IDataValidationService
{
    /// <summary>
    /// 验证接收到的数据是否有效
    /// </summary>
    /// <param name="data">原始字节数据</param>
    /// <param name="portName">串口名称</param>
    /// <returns>验证结果</returns>
    Task<DataValidationResult> ValidateDataAsync(byte[] data, string portName);

    /// <summary>
    /// 检查是否需要触发波特率重新检测
    /// </summary>
    /// <param name="portName">串口名称</param>
    /// <returns>是否需要重新检测</returns>
    Task<bool> ShouldTriggerBaudRateDetectionAsync(string portName);

    /// <summary>
    /// 重置指定串口的验证状态
    /// </summary>
    /// <param name="portName">串口名称</param>
    void ResetValidationState(string portName);

    /// <summary>
    /// 获取串口的数据质量统计
    /// </summary>
    /// <param name="portName">串口名称</param>
    /// <returns>数据质量统计</returns>
    DataQualityStatistics GetDataQualityStatistics(string portName);
}

/// <summary>
/// 数据验证结果
/// </summary>
public class DataValidationResult
{
    /// <summary>
    /// 数据是否有效
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// 数据质量评分 (0.0 - 1.0)
    /// </summary>
    public double QualityScore { get; set; }

    /// <summary>
    /// 建议的操作
    /// </summary>
    public ValidationAction SuggestedAction { get; set; }

    /// <summary>
    /// 验证消息
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 处理后的数据（如果需要清理）
    /// </summary>
    public byte[]? ProcessedData { get; set; }

    /// <summary>
    /// 是否应该丢弃此数据
    /// </summary>
    public bool ShouldDiscard { get; set; }
}

/// <summary>
/// 建议的验证操作
/// </summary>
public enum ValidationAction
{
    /// <summary>
    /// 正常处理
    /// </summary>
    Normal,

    /// <summary>
    /// 清理数据后处理
    /// </summary>
    CleanAndProcess,

    /// <summary>
    /// 丢弃数据
    /// </summary>
    Discard,

    /// <summary>
    /// 触发波特率重新检测
    /// </summary>
    TriggerBaudRateDetection,

    /// <summary>
    /// 暂停数据处理
    /// </summary>
    PauseProcessing
}

/// <summary>
/// 数据质量统计
/// </summary>
public class DataQualityStatistics
{
    /// <summary>
    /// 总接收数据包数
    /// </summary>
    public int TotalPackets { get; set; }

    /// <summary>
    /// 有效数据包数
    /// </summary>
    public int ValidPackets { get; set; }

    /// <summary>
    /// 无效数据包数
    /// </summary>
    public int InvalidPackets { get; set; }

    /// <summary>
    /// 平均质量评分
    /// </summary>
    public double AverageQualityScore { get; set; }

    /// <summary>
    /// 连续无效数据包数
    /// </summary>
    public int ConsecutiveInvalidPackets { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public System.DateTime LastUpdateTime { get; set; }

    /// <summary>
    /// 数据质量趋势 (改善/稳定/下降)
    /// </summary>
    public DataQualityTrend Trend { get; set; }
}

/// <summary>
/// 数据质量趋势
/// </summary>
public enum DataQualityTrend
{
    /// <summary>
    /// 改善
    /// </summary>
    Improving,

    /// <summary>
    /// 稳定
    /// </summary>
    Stable,

    /// <summary>
    /// 下降
    /// </summary>
    Deteriorating
}