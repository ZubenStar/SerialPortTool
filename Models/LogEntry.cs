using System;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 日志条目模型
/// </summary>
public class LogEntry
{
    /// <summary>
    /// 日志ID
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 串口名称
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// 日志级别
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>
    /// 日志内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 原始字节数据
    /// </summary>
    public byte[]? RawData { get; set; }

    /// <summary>
    /// 数据格式
    /// </summary>
    public DataFormat Format { get; set; } = DataFormat.Text;

    /// <summary>
    /// 是否为接收数据(false为发送数据)
    /// </summary>
    public bool IsReceived { get; set; } = true;

    /// <summary>
    /// 转换为字符串表示
    /// </summary>
    public override string ToString()
    {
        var direction = IsReceived ? "RX" : "TX";
        return $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{PortName}] [{direction}] {Content}";
    }
}