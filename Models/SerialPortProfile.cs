using SerialPortTool.Helpers;

namespace SerialPortTool.Models;

/// <summary>
/// 一组串口线路参数的<b>纯数值</b>投影：命名预设（v2.5.0）与启动恢复（v2.5.0）共用的持久化形状。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SerialPortConfig"/> cannot serve here, even though it carries the same information: it is
/// typed with the <c>System.IO.Ports</c> enums, so touching it pulls that assembly into whoever reads the
/// value — and the logic-layer test project deliberately has no such reference (see the note in its
/// <c>.csproj</c>). Keeping the stored shape as plain ints is what lets the validation below be unit
/// tested instead of only being reachable through a dialog.
/// </para>
/// <para>
/// The int values ARE the <c>System.IO.Ports</c> numeric values. That is part of the storage format, not a
/// coincidence: renaming them would silently re-interpret every saved preset. The mapping back to the
/// enums lives in exactly one place, <c>Services/PortProfileMapper</c>.
/// </para>
/// <para>
/// This is deliberately a <em>subset</em> of <see cref="SerialPortConfig"/>: the line parameters, the text
/// encoding and the line ending. Auto-reconnect, the read/write timeouts and the colour slot are left out
/// on purpose — each already has its own single owner (the config defaults, and
/// <c>PortColor_&lt;port&gt;</c>), and duplicating them here would create a second source of truth for
/// values that were never part of "a baud rate preset".
/// </para>
/// </remarks>
public sealed class SerialPortProfile
{
    /// <summary>波特率。</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>数据位。取值 5/6/7/8。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>停止位。取值即 <c>System.IO.Ports.StopBits</c>：1=One，2=Two，3=OnePointFive。</summary>
    public int StopBits { get; set; } = 1;

    /// <summary>校验位。取值即 <c>System.IO.Ports.Parity</c>：0=None，1=Odd，2=Even，3=Mark，4=Space。</summary>
    public int Parity { get; set; } = 0;

    /// <summary>流控。取值即 <c>System.IO.Ports.Handshake</c>：0=None，1=XOnXOff，2=RequestToSend，3=Both。</summary>
    public int Handshake { get; set; } = 0;

    /// <summary>文本编码名。解析与兜底规则见 <see cref="SerialEncodings.Normalize"/>。</summary>
    public string TextEncodingName { get; set; } = SerialEncodings.Utf8Name;

    /// <summary>行尾符。取值即 <see cref="Core.Enums.SendLineEnding"/>：0=None，1=Cr，2=Lf，3=CrLf。</summary>
    public int LineEnding { get; set; } = 0;

    /// <summary>逐成员复制。</summary>
    public SerialPortProfile Clone() => new()
    {
        BaudRate = BaudRate,
        DataBits = DataBits,
        StopBits = StopBits,
        Parity = Parity,
        Handshake = Handshake,
        TextEncodingName = TextEncodingName,
        LineEnding = LineEnding,
    };
}
