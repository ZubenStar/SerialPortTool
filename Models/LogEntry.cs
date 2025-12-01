using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 日志条目模型
/// </summary>
public partial class LogEntry : ObservableObject
{
    /// <summary>
    /// 日志ID
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 时间戳
    /// </summary>
    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    /// <summary>
    /// 串口名称
    /// </summary>
    [ObservableProperty]
    private string _portName = string.Empty;

    /// <summary>
    /// 日志级别
    /// </summary>
    [ObservableProperty]
    private LogLevel _level = LogLevel.Info;

    /// <summary>
    /// 日志内容
    /// </summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>
    /// 原始字节数据
    /// </summary>
    [ObservableProperty]
    private byte[]? _rawData;

    /// <summary>
    /// 数据格式
    /// </summary>
    [ObservableProperty]
    private DataFormat _format = DataFormat.Text;

    /// <summary>
    /// 是否为接收数据(false为发送数据)
    /// </summary>
    [ObservableProperty]
    private bool _isReceived = true;

    /// <summary>
    /// 格式化文本用于显示 (高性能访问)
    /// </summary>
    public string FormattedText => $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{PortName}] {Content}";

    /// <summary>
    /// 转换为字符串表示
    /// </summary>
    public override string ToString()
    {
        var direction = IsReceived ? "RX" : "TX";
        return $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{PortName}] [{direction}] {Content}";
    }
}