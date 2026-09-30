namespace SerialPortTool.Models;

/// <summary>
/// 一个串口的本地备注 / 标签 / 分组。
/// </summary>
/// <remarks>
/// <para>
/// Per-port rather than per-machine, following <c>PortEncoding_&lt;port&gt;</c>: the same physical adapter
/// is not always the same COM number, but within one machine the number is what the user has in front of
/// them, and a note written against the device must appear against the row for it.
/// </para>
/// <para>
/// The group is a plain string field and there is no group table. That is the <c>SendSnippet.Group</c>
/// shape again: a group comes into existence when a port names one and disappears with it, so there is no
/// empty group to show and no dangling reference to prune.
/// </para>
/// <para>
/// Nothing here is sent, logged or exported. It is notes to yourself, and treating it as anything else
/// would leak a reminder like 「这台是坏的那台」 into a file log shipped to someone else.
/// </para>
/// </remarks>
public sealed class PortMetadata
{
    /// <summary>备注的字符数上限。它是便签不是文档，而且整份都要进 settings.json。</summary>
    public const int MaxNotesLength = 200;

    /// <summary>标签串的字符数上限；切分与去重规则见 <see cref="Helpers.PortTags"/>。</summary>
    public const int MaxTagsLength = 160;

    /// <summary>分组名的字符数上限。</summary>
    public const int MaxGroupLength = 32;

    /// <summary>串口名。大小写不敏感地参与查找（<c>COM3</c> 与 <c>com3</c> 是同一个口）。</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>自由文本备注。</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>标签，输入框里的原样串（拆分在需要显示/搜索时才做）。</summary>
    public string Tags { get; set; } = string.Empty;

    /// <summary>分组名，空串表示未分组。</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>True when there is anything worth showing — a blank entry must not be persisted.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Notes) &&
        string.IsNullOrWhiteSpace(Tags) &&
        string.IsNullOrWhiteSpace(Group);

    /// <summary>逐成员复制。</summary>
    public PortMetadata Clone() => new()
    {
        PortName = PortName,
        Notes = Notes,
        Tags = Tags,
        Group = Group,
    };
}
