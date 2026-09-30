using System;
using System.Collections.Generic;

namespace SerialPortTool.Helpers;

/// <summary>
/// 端口标签（tags）的归一化与 Fees 拆分规则。
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project — split out rather than
/// left inline in the dialog that edits it, because "what counts as a tag" is the part worth pinning down
/// (separators, duplicates, case, cap) and the dialog only supplies the string.
/// </para>
/// <para>
/// Deliberately small: tags are a labelling aid on top of the port name, not a taxonomy. Everything
/// beyond split/normalise/dedupe belongs to whoever searches them.
/// </para>
/// </remarks>
public static class PortTags
{
    /// <summary>Separators a user may reasonably type between tags.</summary>
    private static readonly char[] Separators = { ',', '，', ';', '；', ' ' };

    /// <summary>一条记录允许的标签数上限。</summary>
    public const int MaxTags = 6;

    /// <summary>单个标签的字符数上限。</summary>
    public const int MaxTagLength = 16;

    /// <summary>
    /// 把输入框里的一串文字切成去重后的标签列表。
    /// </summary>
    /// <returns>已去空白、已去重（大小写不敏感）的标签，顺序保留用户书写顺序。</returns>
    public static IReadOnlyList<string> Split(string? tags)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(tags))
        {
            return result;
        }

        foreach (var piece in tags.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length > MaxTagLength)
            {
                trimmed = trimmed[..MaxTagLength];
            }

            if (!Contains(result, trimmed))
            {
                result.Add(trimmed);

                if (result.Count >= MaxTags)
                {
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 把标签列表拼回输入框里的显示形式。
    /// </summary>
    public static string Join(IEnumerable<string> tags) => string.Join(", ", tags);

    /// <summary>大小写不敏感的存在性判断，避免同一个标签写成两种大小写。</summary>
    private static bool Contains(List<string> tags, string candidate)
    {
        foreach (var existing in tags)
        {
            if (string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
