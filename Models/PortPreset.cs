using SerialPortTool.Helpers;

namespace SerialPortTool.Models;

/// <summary>
/// 一个命名串口配置档案：给一组线路参数起个名字，之后一键套用。
/// </summary>
/// <remarks>
/// <para>
/// The library lives in one <c>settings.json</c> string key the way the quick-send library and the
/// highlight rules already do — see the note on <c>SnippetService.LibrarySettingKey</c> for why that shape
/// rather than a file of its own.
/// </para>
/// <para>
/// A preset carries the line ending alongside the line parameters on purpose. They are different settings
/// (a per-app send preference vs. per-port open parameters), so nothing forces them together — but a preset
/// that changed the baud rate and left the terminator behind would be the worst kind of half-applied: it
/// looks applied, and the bytes are still wrong. <see cref="SerialPortProfile.LineEnding"/> is what makes
/// 「套用」 mean "the whole device profile".
/// </para>
/// </remarks>
public sealed class PortPreset
{
    /// <summary>名称的字符数上限。它是一个菜单里的标签，不是备注。</summary>
    public const int MaxNameLength = 40;

    /// <summary>唯一标识。同名判断用 <see cref="Services.IPortPresetService.Validate"/>，不参与这里的比较。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示在预设菜单里的名字。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>这套档案的参数。</summary>
    public SerialPortProfile Profile { get; set; } = SerialPortConfigCodec.CreateDefault();

    /// <summary>逐成员复制，避免调用方持有同一份 Profile 引用后互相改到。</summary>
    public PortPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Profile = Profile.Clone(),
    };
}
