using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SerialPortTool.Helpers;

/// <summary>
/// 发送历史：最近发送过的原始内容，最新在前，供发送框 ↑/↓ 召回。
/// </summary>
/// <remarks>
/// <para>
/// Pure logic, UI-free, linked into the logic-layer test project. The whole list round-trips through one
/// <c>settings.json</c> string key as a JSON array — the shape <c>QuickSendSnippets</c> and
/// <c>HighlightRules</c> already use.
/// </para>
/// <para>
/// It deliberately does <b>not</b> follow the search history's <c>string.Join("|")</c> scheme. A send
/// payload is arbitrary text: it can contain a pipe, a newline, or a quote, and every one of those would
/// either split one entry into several or merge several into one. JSON is the format that cannot be
/// confused with its own contents.
/// </para>
/// </remarks>
public static class SendHistory
{
    /// <summary>How many entries are kept. A recall list is a convenience, not an archive.</summary>
    public const int MaxEntries = 10;

    /// <summary>
    /// Returns a copy of <paramref name="current"/> with <paramref name="payload"/> at the front.
    /// </summary>
    /// <remarks>
    /// De-duplicates by exact match (re-sending something moves it to the front rather than adding a second
    /// copy), drops blank payloads, and trims the tail. The input list is not modified — the caller owns
    /// the live instance and swaps contents itself.
    /// </remarks>
    public static List<string> Merge(IReadOnlyList<string> current, string payload)
    {
        var merged = new List<string>(current);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return merged;
        }

        merged.RemoveAll(entry => string.Equals(entry, payload, StringComparison.Ordinal));
        merged.Insert(0, payload);

        if (merged.Count > MaxEntries)
        {
            merged.RemoveRange(MaxEntries, merged.Count - MaxEntries);
        }

        return merged;
    }

    /// <summary>
    /// Parses the persisted array. Never throws and never returns null entries.
    /// </summary>
    /// <returns>
    /// False for a blank or unusable value, so the caller can tell "no history yet" from "the stored
    /// history is corrupt" and say so once in the log.
    /// </returns>
    public static bool TryDeserialize(string? json, out List<string> entries)
    {
        entries = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(json);
            if (parsed is null)
            {
                return false;
            }

            foreach (var entry in parsed)
            {
                if (!string.IsNullOrEmpty(entry))
                {
                    entries.Add(entry);
                }

                if (entries.Count >= MaxEntries)
                {
                    break;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Serialises the list for one settings key.</summary>
    public static string Serialize(IReadOnlyList<string> entries)
        => JsonSerializer.Serialize(entries);
}
