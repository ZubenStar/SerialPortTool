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
    /// 流控（握手）方式
    /// </summary>
    /// <remarks>
    /// Like every other line parameter here it is applied by <c>SerialPort</c> at open time, so changing
    /// it only affects the next open — that is the behaviour the sidebar hints at.
    /// </remarks>
    public Handshake Handshake { get; set; } = Handshake.None;

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
    /// 文本编码名称（用于解码接收数据与编码发送文本）。
    /// </summary>
    /// <remarks>
    /// Carried on the config rather than set right after the open call so the receive path can never see
    /// a chunk decoded with the previous encoding: the service registers this before the handle is
    /// opened. Canonical names live in <see cref="Helpers.SerialEncodings"/>; anything unrecognised
    /// resolves to UTF-8.
    /// </remarks>
    public string TextEncodingName { get; set; } = Helpers.SerialEncodings.Utf8Name;

    /// <summary>
    /// 端口显示颜色槽位（十六进制，浅色变体）。
    /// </summary>
    /// <remarks>
    /// A palette slot, not a rendered colour — see <see cref="PortColorPalette"/>.
    /// </remarks>
    public string ColorHex { get; set; } = PortColorPalette.DefaultRxHex;

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
            Handshake = Handshake,
            AutoReconnect = AutoReconnect,
            ReconnectInterval = ReconnectInterval,
            ReadTimeout = ReadTimeout,
            WriteTimeout = WriteTimeout,
            TextEncodingName = TextEncodingName,
            ColorHex = ColorHex
        };
    }
}