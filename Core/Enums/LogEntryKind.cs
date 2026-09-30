namespace SerialPortTool.Core.Enums;

/// <summary>
/// 一条日志行代表什么。
/// </summary>
/// <remarks>
/// <para>
/// Replaces the old <c>bool IsReceived</c>, which could only say "RX or TX" and therefore had no way to
/// express a row the application generated about the connection itself. Nothing serialises a
/// <c>LogEntry</c>, so this enum is free to grow.
/// </para>
/// <para>
/// The third value is what makes "这几秒为什么没有数据" answerable from the log alone. Before it existed, a
/// port closing, reconnecting or erroring produced no log row at all — the only record was the status bar,
/// which the next message overwrote, and the Serilog application log, which is a different file.
/// </para>
/// </remarks>
public enum LogEntryKind
{
    /// <summary>设备发来的数据。</summary>
    Received,

    /// <summary>本机发出的数据（发送框、快捷指令、Tuning 摘要）。</summary>
    Sent,

    /// <summary>关于连接本身的事件：已连接 / 已断开 / 连接错误 / 端口错误。</summary>
    Event,
}
