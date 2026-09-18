using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SerialPortTool.Models;

/// <summary>
/// 日志条目模型 - 优化版本用于高性能日志处理
/// </summary>
public partial class LogEntry : ObservableObject
{
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

    // `Format` (DataFormat) used to live here. Nothing read it — not the display path, not the file
    // logger, not the filters — and the only other consumer of DataFormat was CommandPreset, itself
    // unused. Both are gone rather than left as an implicit "the app supports formats" contract that
    // no code honours.

    /// <summary>
    /// 是否为接收数据(false为发送数据)
    /// </summary>
    [ObservableProperty]
    private bool _isReceived = true;

    /// <summary>
    /// Hex the row is painted with (already resolved for the active appearance by the ViewModel).
    /// </summary>
    /// <remarks>
    /// Empty by default on purpose: every construction site assigns a colour, so anything that ends up
    /// blank is a missed assignment that <c>HexColorToBrushConverter</c> surfaces as the themed default
    /// text brush instead of the old hard-coded black — which was invisible on the dark palette.
    /// </remarks>
    [ObservableProperty]
    private string _colorHex = string.Empty;

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