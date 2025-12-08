using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 波特率检测服务接口
/// </summary>
public interface IBaudRateDetectorService
{
    /// <summary>
    /// 自动检测最适合的波特率
    /// </summary>
    /// <param name="portName">串口名称</param>
    /// <param name="testDurationMs">每个波特率的测试时间(毫秒)</param>
    /// <returns>检测到的波特率列表，按匹配度排序</returns>
    Task<List<BaudRateDetectionResult>> DetectOptimalBaudRateAsync(string portName, int testDurationMs = 2000);

    /// <summary>
    /// 验证当前波特率是否正确
    /// </summary>
    /// <param name="portName">串口名称</param>
    /// <param name="baudRate">当前波特率</param>
    /// <param name="validationDurationMs">验证时间(毫秒)</param>
    /// <returns>验证结果</returns>
    Task<BaudRateValidationResult> ValidateBaudRateAsync(string portName, int baudRate, int validationDurationMs = 3000);

    /// <summary>
    /// 获取常用的波特率列表
    /// </summary>
    /// <returns>常用波特率列表</returns>
    List<int> GetCommonBaudRates();
}

/// <summary>
/// 波特率检测结果
/// </summary>
public class BaudRateDetectionResult
{
    public int BaudRate { get; set; }
    public double ConfidenceScore { get; set; } // 0.0 - 1.0
    public int ValidDataCount { get; set; }
    public int TotalDataCount { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// 波特率验证结果
/// </summary>
public class BaudRateValidationResult
{
    public bool IsValid { get; set; }
    public double DataQualityScore { get; set; } // 0.0 - 1.0
    public int ValidDataCount { get; set; }
    public int InvalidDataCount { get; set; }
    public string Recommendation { get; set; } = string.Empty;
    public List<int> SuggestedBaudRates { get; set; } = new();
}