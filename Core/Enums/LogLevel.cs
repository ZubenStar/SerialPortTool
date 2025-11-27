namespace SerialPortTool.Core.Enums;

/// <summary>
/// 日志级别枚举
/// </summary>
public enum LogLevel
{
    /// <summary>
    /// 调试信息
    /// </summary>
    Debug = 0,

    /// <summary>
    /// 一般信息
    /// </summary>
    Info = 1,

    /// <summary>
    /// 警告信息
    /// </summary>
    Warning = 2,

    /// <summary>
    /// 错误信息
    /// </summary>
    Error = 3,

    /// <summary>
    /// 致命错误
    /// </summary>
    Fatal = 4
}