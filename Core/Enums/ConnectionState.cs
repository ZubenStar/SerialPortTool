namespace SerialPortTool.Core.Enums;

/// <summary>
/// 串口连接状态枚举
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// 已断开
    /// </summary>
    Disconnected,

    /// <summary>
    /// 连接中
    /// </summary>
    Connecting,

    /// <summary>
    /// 已连接
    /// </summary>
    Connected,

    /// <summary>
    /// 正在断开
    /// </summary>
    Disconnecting,

    /// <summary>
    /// 连接错误
    /// </summary>
    Error
}