using System;
using System.Collections.Generic;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Helpers;

/// <summary>
/// 连接事件如何变成日志行，以及同一行重复时如何折叠成计数。
/// </summary>
/// <remarks>
/// UI-free and dependency-free so it can be linked into the logic-layer test project — which matters here
/// because the aggregation window is the part of this feature that is both easy to get wrong and impossible
/// to see: too eager and a single frame error reads as a storm, too lax and the storm drowns the data it is
/// supposed to explain.
/// </remarks>
public static class LogEventText
{
    /// <summary>
    /// 状态变化对应的行内容。
    /// </summary>
    /// <remarks>
    /// No port name in the text: the row already reads <c>[HH:mm:ss.fff] [COM3] [SYS] …</c>, so repeating it
    /// would be noise — the same way an RX row carries only the payload.
    /// </remarks>
    public static string DescribeStateChange(ConnectionState newState) => newState switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Disconnected => "已断开",
        ConnectionState.Error => "连接错误",
        _ => newState.ToString(),
    };

    /// <summary>
    /// 端口错误对应的行内容。
    /// </summary>
    /// <remarks>
    /// The message is the OS/driver text verbatim, untranslated and untrimmed: when a port misbehaves that
    /// wording is the evidence, and rewriting it would put a translator between the reader and the cause.
    /// </remarks>
    public static string DescribeError(string message)
        => string.IsNullOrWhiteSpace(message) ? "端口错误" : $"错误：{message}";

    /// <summary>
    /// 判断两次出现是否"同一件事"的聚合键。
    /// </summary>
    /// <remarks>
    /// Port plus message, joined with a unit separator because it cannot occur in either. Keyed on the port
    /// as well as the text because two ports reporting the same driver error are two facts, not one. Not
    /// keyed on the rendered line: the repeat suffix is part of that line, so keying on it would make every
    /// repeat a new key and no window would ever collapse anything.
    /// </remarks>
    public static string AggregateKey(string portName, string message)
        => $"{portName}\u001F{message}";

    /// <summary>把重复次数附加到行内容上。</summary>
    public static string WithRepeatCount(string line, int repeats, int windowSeconds)
        => $"{line}（最近 {windowSeconds} 秒内重复 {repeats} 次）";
}

/// <summary>
/// 一个窗口内同一事件行的重复计数。
/// </summary>
/// <remarks>
/// <para>
/// The caller owns the clock: it records every occurrence and drains when its own timer expires. Keeping the
/// time source out of here is what makes the class testable without a dispatcher, and it matches how the
/// flush loop already separates "decide the cadence" (a timer on the UI thread) from "apply the batch".
/// </para>
/// <para>
/// A list rather than a dictionary: the number of distinct lines in one window is a handful, and insertion
/// order is what makes the summaries come out in the order the events did.
/// </para>
/// </remarks>
public sealed class EventAggregateWindow
{
    private readonly List<(string Key, string PortName, string Line, int Count)> _entries = new();

    /// <summary>还有其他键在等待 drain。</summary>
    public bool HasPending => _entries.Count > 0;

    /// <summary>
    /// 记录一次出现。
    /// </summary>
    /// <returns>
    /// True when this is the first occurrence of the key in this window, i.e. the caller should emit the line
    /// now. False means "collapsed as a repeat" — emit nothing, and the count surfaces when the window
    /// drains.
    /// </returns>
    public bool Record(string key, string portName, string line)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal))
            {
                var existing = _entries[i];
                _entries[i] = (existing.Key, existing.PortName, existing.Line, existing.Count + 1);
                return false;
            }
        }

        _entries.Add((key, portName, line, 1));
        return true;
    }

    /// <summary>
    /// 清空计数并返回出现过一次以上的那些行，按首次出现的顺序。
    /// </summary>
    /// <remarks>
    /// Single occurrences are dropped rather than returned: their line has already been emitted, and a
    /// "重复 1 次" summary would be pure noise.
    /// </remarks>
    public List<(string PortName, string Line, int Count)> DrainRepeats()
    {
        var repeats = new List<(string, string, int)>();

        foreach (var entry in _entries)
        {
            if (entry.Count > 1)
            {
                repeats.Add((entry.PortName, entry.Line, entry.Count));
            }
        }

        _entries.Clear();
        return repeats;
    }
}
