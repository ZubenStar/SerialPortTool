namespace SerialPortTool.Core.Enums;

/// <summary>
/// 串口连接状态枚举
/// </summary>
/// <remarks>
/// Only the three states the app actually raises are kept. <c>Connecting</c> and <c>Disconnecting</c>
/// were declared but never assigned — an enum that documents transitions the code never sends is worse
/// than no documentation, because readers reason about a state machine that does not exist.
/// </remarks>
public enum ConnectionState
{
    /// <summary>
    /// 已断开
    /// </summary>
    Disconnected,

    /// <summary>
    /// 已连接
    /// </summary>
    Connected,

    /// <summary>
    /// 连接错误
    /// </summary>
    Error
}