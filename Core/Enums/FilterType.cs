namespace SerialPortTool.Core.Enums;

/// <summary>
/// 过滤器类型枚举
/// </summary>
public enum FilterType
{
    /// <summary>
    /// 文本匹配
    /// </summary>
    Text,

    /// <summary>
    /// 正则表达式
    /// </summary>
    Regex,

    /// <summary>
    /// 日志级别过滤
    /// </summary>
    LogLevel,

    /// <summary>
    /// 串口名称过滤
    /// </summary>
    PortName
}