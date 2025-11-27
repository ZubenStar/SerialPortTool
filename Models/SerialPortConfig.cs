using System.IO.Ports;

namespace SerialPortTool.Models;

/// <summary>
/// 串口配置模型
/// </summary>
public class SerialPortConfig
{
    /// <summary>
    /// 串口名称 (例如: COM3)
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// 波特率
    /// </summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>
    /// 数据位
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// 停止位
    /// </summary>
    public StopBits StopBits { get; set; } = StopBits.One;

    /// <summary>
    /// 校验位
    /// </summary>
    public Parity Parity { get; set; } = Parity.None;

    /// <summary>
    /// 自动重连
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// 重连间隔(毫秒)
    /// </summary>
    public int ReconnectInterval { get; set; } = 3000;

    /// <summary>
    /// 读取超时(毫秒)
    /// </summary>
    public int ReadTimeout { get; set; } = 500;

    /// <summary>
    /// 写入超时(毫秒)
    /// </summary>
    public int WriteTimeout { get; set; } = 500;

    /// <summary>
    /// 克隆配置
    /// </summary>
    public SerialPortConfig Clone()
    {
        return new SerialPortConfig
        {
            PortName = PortName,
            BaudRate = BaudRate,
            DataBits = DataBits,
            StopBits = StopBits,
            Parity = Parity,
            AutoReconnect = AutoReconnect,
            ReconnectInterval = ReconnectInterval,
            ReadTimeout = ReadTimeout,
            WriteTimeout = WriteTimeout
        };
    }
}