using System;
using System.Threading.Tasks;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 文件日志服务接口
/// </summary>
public interface IFileLoggerService
{
    /// <summary>
    /// 开始记录指定串口的日志
    /// </summary>
    Task StartLoggingAsync(string portName);

    /// <summary>
    /// 停止记录指定串口的日志
    /// </summary>
    Task StopLoggingAsync(string portName);

    /// <summary>
    /// 写入日志条目
    /// </summary>
    Task WriteLogAsync(string portName, LogEntry entry);

    /// <summary>
    /// 获取日志文件路径
    /// </summary>
    string GetLogFilePath(string portName);

    /// <summary>
    /// 检查是否正在记录日志
    /// </summary>
    bool IsLogging(string portName);
}