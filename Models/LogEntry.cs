using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 日志条目模型 - 优化版本用于高性能日志处理
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
    /// 显示颜色的十六进制值
    /// </summary>
    [ObservableProperty]
    private string _colorHex = "#000000";

    // 缓存格式化文本以避免重复字符串分配
    private string? _cachedFormattedText;

    /// <summary>
    /// 格式化文本用于显示 (高性能访问,带缓存)
    /// </summary>
    public string FormattedText
    {
        get
        {
            if (_cachedFormattedText == null)
            {
                var direction = IsReceived ? "RX" : "TX";
                _cachedFormattedText = $"[{Timestamp:HH:mm:ss.fff}] [{PortName}] [{direction}] {Content}";
            }
            return _cachedFormattedText;
        }
    }

    /// <summary>
    /// 转换为字符串表示
    /// </summary>
    public override string ToString()
    {
        var direction = IsReceived ? "RX" : "TX";
        return $"[{Timestamp:HH:mm:ss.fff}] [{PortName}] [{direction}] {Content}";
    }

    /// <summary>
    /// 清除缓存的格式化文本(当属性变化时调用)
    /// </summary>
    partial void OnContentChanged(string value) => _cachedFormattedText = null;
    partial void OnPortNameChanged(string value) => _cachedFormattedText = null;
    partial void OnTimestampChanged(DateTime value) => _cachedFormattedText = null;
    partial void OnIsReceivedChanged(bool value) => _cachedFormattedText = null;
}