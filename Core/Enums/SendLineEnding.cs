namespace SerialPortTool.Core.Enums;

/// <summary>
/// 发送文本时自动追加的行尾符。
/// </summary>
/// <remarks>
/// The values are persisted as ints, so the numbering is part of the settings format — append new members
/// instead of reordering, and keep <see cref="None"/> at 0 so an untouched install keeps sending exactly
/// the bytes it always did.
/// </remarks>
public enum SendLineEnding
{
    /// <summary>不追加任何内容（默认）。发送框里的字节与手工输入的完全一致。</summary>
    None = 0,

    /// <summary>回车 <c>\r</c>（0x0D）。多数串口控制台用它作为一条命令的结束。</summary>
    Cr = 1,

    /// <summary>换行 <c>\n</c>（0x0A）。</summary>
    Lf = 2,

    /// <summary>回车换行 <c>\r\n</c>（0x0D 0x0A）。</summary>
    CrLf = 3,
}
