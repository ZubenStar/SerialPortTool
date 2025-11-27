namespace SerialPortTool.Models;

/// <summary>
/// 串口统计信息模型
/// </summary>
public class PortStatistics
{
    /// <summary>
    /// 串口名称
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// 接收字节数
    /// </summary>
    public long ReceivedBytes { get; set; }

    /// <summary>
    /// 发送字节数
    /// </summary>
    public long SentBytes { get; set; }

    /// <summary>
    /// 接收消息数
    /// </summary>
    public long ReceivedMessages { get; set; }

    /// <summary>
    /// 发送消息数
    /// </summary>
    public long SentMessages { get; set; }

    /// <summary>
    /// 错误次数
    /// </summary>
    public long ErrorCount { get; set; }

    /// <summary>
    /// 连接时间
    /// </summary>
    public DateTime? ConnectedAt { get; set; }

    /// <summary>
    /// 断开时间
    /// </summary>
    public DateTime? DisconnectedAt { get; set; }

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