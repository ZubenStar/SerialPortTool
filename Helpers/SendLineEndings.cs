using System.Collections.Generic;
using System.Text;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Helpers;

/// <summary>
/// 把发送框（或快捷指令）里的一段文本，变成真正要写到线上的字符串，以及用于日志的逐行视图。
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project. The whole file is a
/// pure function of its arguments — no state, no service, no allocation beyond the plan it returns.
/// </para>
/// <para>
/// The reason this exists at all: the send box is a single-line <c>TextBox</c>, so a user has no way to
/// type a carriage return. Before this, a payload that needed to end in <c>\r</c> could only be produced
/// by typing the escape into a saved snippet. The bytes themselves are still "whatever is in the box"
/// when no terminator is selected, which is what keeps the default behaviour unchanged.
/// </para>
/// </remarks>
public static class SendLineEndings
{
    /// <summary>The characters <paramref name="lineEnding"/> appends; empty for <see cref="SendLineEnding.None"/>.</summary>
    public static string Terminator(SendLineEnding lineEnding) => lineEnding switch
    {
        SendLineEnding.Cr => "\r",
        SendLineEnding.Lf => "\n",
        SendLineEnding.CrLf => "\r\n",
        _ => string.Empty,
    };

    /// <summary>
    /// Builds the exact string to write, plus the logical lines a sent-log entry is made from.
    /// </summary>
    /// <param name="text">What the send box (or the snippet) contains.</param>
    /// <param name="lineEnding">Terminator to append.</param>
    /// <param name="splitMultiline">
    /// When true and a terminator is selected, the payload is split on newlines and every line gets its own
    /// terminator — the shape a device expects from a multi-line paste. When false, the text goes out as
    /// one block with a single terminator at the end.
    /// </param>
    public static SendPlan CreatePlan(string text, SendLineEnding lineEnding, bool splitMultiline)
    {
        var (segments, displayLines) = Build(text, lineEnding, splitMultiline);
        return new SendPlan(string.Concat(segments), segments, displayLines);
    }

    /// <summary>
    /// Splits one payload into the pieces a line-delayed send writes one at a time.
    /// </summary>
    /// <remarks>
    /// Concatenating what this returns reproduces <see cref="CreatePlan"/>'s payload <em>exactly</em>, because
    /// both come out of the same <see cref="Build"/> call — that identity is what makes the F7 line delay
    /// safe to add: with the interval at zero the caller sends the whole concatenation in one write, so the
    /// bytes on the wire cannot differ from an install that never turned the delay on.
    /// </remarks>
    public static IReadOnlyList<string> CreateLineSequence(string text, SendLineEnding lineEnding, bool splitMultiline)
        => Build(text, lineEnding, splitMultiline).Segments;

    /// <summary>
    /// The one place the payload shape is decided. Both public entry points go through it so the single-write
    /// and split-write results cannot drift apart.
    /// </summary>
    private static (List<string> Segments, string[] DisplayLines) Build(
        string text,
        SendLineEnding lineEnding,
        bool splitMultiline)
    {
        var terminator = Terminator(lineEnding);

        if (terminator.Length == 0)
        {
            // No terminator selected: send exactly what is in the box. Splitting would be pointless (the
            // join would reproduce the input) and would risk changing bytes on an install that never
            // asked for this feature.
            return (new List<string> { text }, new[] { text });
        }

        if (!splitMultiline)
        {
            return (new List<string> { text + terminator }, new[] { text });
        }

        // \r\n first, so a Windows-style line break is not counted as two line breaks.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        // A single trailing line break is the terminator of the last line, not an extra empty line.
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            lines = lines[..^1];
        }

        var builder = new StringBuilder(normalized.Length + terminator.Length * lines.Length);
        var segments = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            builder.Append(line).Append(terminator);
            segments.Add(line + terminator);
        }

        return (segments, lines);
    }
}

/// <summary>
/// One text-mode send: the string to encode, the pieces it is made of, and the lines the sent-log shows.
/// </summary>
/// <remarks>
/// <see cref="Payload"/> is deliberately still a <em>string</em> rather than bytes: the same send can go to
/// several ports that are configured for different character sets, so encoding is the caller's per-port
/// step and not part of planning.
/// </remarks>
public sealed class SendPlan
{
    public SendPlan(string payload, IReadOnlyList<string> segments, IReadOnlyList<string> displayLines)
    {
        Payload = payload;
        Segments = segments;
        DisplayLines = displayLines;
    }

    /// <summary>Exactly what to encode and write, terminators included.</summary>
    public string Payload { get; }

    /// <summary>
    /// 逐行发送时一次写一格的片段；按顺序拼接即 <see cref="Payload"/>。
    /// </summary>
    /// <remarks>
    /// The pieces a line-delayed send (v2.5.0) writes one at a time. Their concatenation is
    /// <see cref="Payload"/> and their count matches <see cref="DisplayLines"/>, so a caller either sends
    /// the payload whole or walks the two lists in lockstep.
    /// </remarks>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Logical lines, without terminators, for the sent-log entries.</summary>
    public IReadOnlyList<string> DisplayLines { get; }
}
