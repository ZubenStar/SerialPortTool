using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using System.IO.Ports;

namespace SerialPortTool.Services;

/// <summary>
/// <see cref="SerialPortProfile"/>（纯 int，可持久化、可单测）与 <see cref="SerialPortConfig"/>
/// （带 <c>System.IO.Ports</c> 枚举）之间的唯一转换点。
/// </summary>
/// <remarks>
/// The split exists because the stored shape must be readable by the logic-layer test project, which
/// deliberately has no <c>System.IO.Ports</c> reference. Having exactly one place that crosses that line is
/// what keeps it from being crossed accidentally somewhere else — a <c>(StopBits)someInt</c> cast written
/// inline in a ViewModel is how an unvalidated value eventually reaches <c>SerialPort.Open</c>.
/// Everything leaving this class therefore goes through
/// <see cref="SerialPortConfigCodec.Sanitize"/> first.
/// </remarks>
public static class PortProfileMapper
{
    /// <summary>把一份配置 + 行尾符转成可持久化的 profile。</summary>
    public static SerialPortProfile FromConfig(SerialPortConfig config, SendLineEnding lineEnding) => new()
    {
        BaudRate = config.BaudRate,
        DataBits = config.DataBits,
        StopBits = (int)config.StopBits,
        Parity = (int)config.Parity,
        Handshake = (int)config.Handshake,
        TextEncodingName = config.TextEncodingName,
        LineEnding = (int)lineEnding,
    };

    /// <summary>
    /// 把 profile 转成串口配置。
    /// </summary>
    /// <remarks>
    /// The values come from storage or from a hand-edited file, so they are sanitised on the way out —
    /// every field lands on something <c>SerialPort</c> accepts. <see cref="SerialPortConfig.AutoReconnect"/>
    /// and the timeouts are intentionally left at their model defaults: a preset describes the wire format,
    /// not how long to wait for the device.
    /// </remarks>
    public static SerialPortConfig ToConfig(SerialPortProfile profile, string portName)
    {
        var safe = SerialPortConfigCodec.Sanitize(profile);

        return new SerialPortConfig
        {
            PortName = portName,
            BaudRate = safe.BaudRate,
            DataBits = safe.DataBits,
            StopBits = (StopBits)safe.StopBits,
            Parity = (Parity)safe.Parity,
            Handshake = (Handshake)safe.Handshake,
            TextEncodingName = safe.TextEncodingName,
        };
    }

    /// <summary>profile 里记录的行尾符；越界值收敛为 <see cref="SendLineEnding.None"/>。</summary>
    public static SendLineEnding ToLineEnding(SerialPortProfile profile)
        => (SendLineEnding)SerialPortConfigCodec.NormalizeLineEnding(profile?.LineEnding ?? 0);
}
