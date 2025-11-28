using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SerialPortTool.Models;

/// <summary>
/// 串口统计信息模型
/// </summary>
public partial class PortStatistics : ObservableObject
{
    /// <summary>
    /// 串口名称
    /// </summary>
    [ObservableProperty]
    private string _portName = string.Empty;

    /// <summary>
    /// 接收字节数
    /// </summary>
    [ObservableProperty]
    private long _receivedBytes;

    /// <summary>
    /// 发送字节数
    /// </summary>
    [ObservableProperty]
    private long _sentBytes;

    /// <summary>
    /// 接收消息数
    /// </summary>
    [ObservableProperty]
    private long _receivedMessages;

    /// <summary>
    /// 发送消息数
    /// </summary>
    [ObservableProperty]
    private long _sentMessages;

    /// <summary>
    /// 错误次数
    /// </summary>
    [ObservableProperty]
    private long _errorCount;

    /// <summary>
    /// 连接时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _connectedAt;

    /// <summary>
    /// 断开时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _disconnectedAt;

    /// <summary>
    /// 连接持续时间
    /// </summary>
    public TimeSpan ConnectionDuration
    {
        get
        {
            if (ConnectedAt == null) return TimeSpan.Zero;
            var endTime = DisconnectedAt ?? DateTime.Now;
            return endTime - ConnectedAt.Value;
        }
    }

    /// <summary>
    /// 平均接收速率 (字节/秒)
    /// </summary>
    public double AverageReceiveRate
    {
        get
        {
            var duration = ConnectionDuration.TotalSeconds;
            return duration > 0 ? ReceivedBytes / duration : 0;
        }
    }

    /// <summary>
    /// 平均发送速率 (字节/秒)
    /// </summary>
    public double AverageSendRate
    {
        get
        {
            var duration = ConnectionDuration.TotalSeconds;
            return duration > 0 ? SentBytes / duration : 0;
        }
    }

    /// <summary>
    /// 错误率
    /// </summary>
    public double ErrorRate
    {
        get
        {
            var total = ReceivedMessages + SentMessages;
            return total > 0 ? (double)ErrorCount / total : 0;
        }
    }

    /// <summary>
    /// 重置统计信息
    /// </summary>
    public void Reset()
    {
        ReceivedBytes = 0;
        SentBytes = 0;
        ReceivedMessages = 0;
        SentMessages = 0;
        ErrorCount = 0;
        ConnectedAt = null;
        DisconnectedAt = null;
    }
}