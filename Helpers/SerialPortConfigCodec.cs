using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SerialPortTool.Helpers;

/// <summary>
/// 串口参数的取值域校验，以及 <c>settings.json</c> 里单 string key 结构化载荷的通用解析。
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project. Every member is
/// total: nothing here throws for a hostile or merely hand-edited input. That is not defensive padding —
/// both callers are convenience features (a preset library and a startup restore), and the one thing they
/// must never do is turn a bad settings value into a failed launch. An unusable value is replaced by the
/// default, which is always something <c>SerialPort</c> will accept.
/// </para>
/// <para>
/// Why the value domains are restated as ints rather than read from the <c>System.IO.Ports</c> enums:
/// this file is compiled into a project that has no <c>System.IO.Ports</c> reference. The restatement is
/// small and static (the enum's own numbering is part of the storage format, see
/// <see cref="SerialPortProfile"/>), and it is exactly what makes "a hand-edited settings.json naming
/// StopBits.None gets rejected before it can reach Open" a testable statement instead of a comment.
/// </para>
/// </remarks>
public static class SerialPortConfigCodec
{
    /// <summary>波特率下界。0 会让 <c>SerialPort</c> 抛 <c>ArgumentOutOfRangeException</c>。</summary>
    public const int MinBaudRate = 1;

    /// <summary>波特率上界，与侧栏「自定义波特率」能输入的最高值同一量级。</summary>
    public const int MaxBaudRate = 12_000_000;

    /// <summary>兜底波特率。</summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>数据位取值域。</summary>
    public static readonly int[] AllowedDataBits = { 5, 6, 7, 8 };

    /// <summary>
    /// 停止位取值域。
    /// </summary>
    /// <remarks>
    /// <c>StopBits.None = 0</c> is absent on purpose: <c>SerialPort</c> refuses it at open time, so a value
    /// a hand-edited file could easily contain must be normalised away rather than passed through.
    /// </remarks>
    public static readonly int[] AllowedStopBits = { 1, 2, 3 };

    /// <summary>校验位取值域。</summary>
    public static readonly int[] AllowedParity = { 0, 1, 2, 3, 4 };

    /// <summary>流控取值域。</summary>
    public static readonly int[] AllowedHandshake = { 0, 1, 2, 3 };

    /// <summary>行尾符取值域。</summary>
    public static readonly int[] AllowedLineEndings = { 0, 1, 2, 3 };

    /// <summary>新建记录的默认参数。</summary>
    public static SerialPortProfile CreateDefault() => new();

    /// <summary>把任一/batch 轴上的越界值收敛到合法值。</summary>
    public static int NormalizeBaudRate(int value)
        => value >= MinBaudRate && value <= MaxBaudRate ? value : DefaultBaudRate;

    /// <summary>把数据位收敛到 {5,6,7,8}。</summary>
    public static int NormalizeDataBits(int value) => PickAllowed(value, AllowedDataBits, 8);

    /// <summary>把停止位收敛到 <see cref="AllowedStopBits"/>。</summary>
    public static int NormalizeStopBits(int value) => PickAllowed(value, AllowedStopBits, 1);

    /// <summary>把校验位收敛到 <see cref="AllowedParity"/>。</summary>
    public static int NormalizeParity(int value) => PickAllowed(value, AllowedParity, 0);

    /// <summary>把流控收敛到 <see cref="AllowedHandshake"/>。</summary>
    public static int NormalizeHandshake(int value) => PickAllowed(value, AllowedHandshake, 0);

    /// <summary>把行尾符收敛到 <see cref="AllowedLineEndings"/>。</summary>
    public static int NormalizeLineEnding(int value) => PickAllowed(value, AllowedLineEndings, 0);

    /// <summary>
    /// 逐字段校验一个 profile，返回<b>一定合法</b>的副本。
    /// </summary>
    /// <remarks>
    /// Never throws, including for <c>null</c> — a JSON object missing its nested <c>Profile</c> member
    /// deserialises to a null property, and that has to cost the user one preset rather than the whole
    /// library.
    /// </remarks>
    public static SerialPortProfile Sanitize(SerialPortProfile? profile)
    {
        if (profile is null)
        {
            return CreateDefault();
        }

        return new SerialPortProfile
        {
            BaudRate = NormalizeBaudRate(profile.BaudRate),
            DataBits = NormalizeDataBits(profile.DataBits),
            StopBits = NormalizeStopBits(profile.StopBits),
            Parity = NormalizeParity(profile.Parity),
            Handshake = NormalizeHandshake(profile.Handshake),
            TextEncodingName = SerialEncodings.Normalize(profile.TextEncodingName),
            LineEnding = NormalizeLineEnding(profile.LineEnding),
        };
    }

    /// <summary>
    /// 解析一个 JSON 数组；损坏、空白或类型不对都返回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by every structured setting this app persists, so the "a corrupt value costs the feature,
    /// not the session" rule is written once. The alternative — each service catching
    /// <see cref="JsonException"/> itself — is how one of them ends up rethrowing from a typo.
    /// </para>
    /// <para>
    /// Only <see cref="JsonException"/> is treated as recoverable. A wrong <c>T</c> would surface as an
    /// <see cref="InvalidOperationException"/> or a <c>NotSupportedException</c> instead, and those are
    /// programming errors that must be visible rather than silently downgraded to "no saved data".
    /// </para>
    /// </remarks>
    public static bool TryParseList<T>(string? json, JsonSerializerOptions options, out List<T> items)
    {
        items = new List<T>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<T>>(json, options);
            if (parsed is null)
            {
                return false;
            }

            items = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 解析「串口名 → 值」形式的 JSON 对象；损坏或空白都返回 <c>false</c>。
    /// </summary>
    /// <remarks>
    /// The map shape only because the natural key is the port name and every reader wants a lookup, not an
    /// order — see <see cref="TryParseList{T}"/> for why failing here is <c>false</c> rather than a throw.
    /// </remarks>
    public static bool TryParseMap<T>(
        string? json,
        JsonSerializerOptions options,
        out Dictionary<string, T> map)
    {
        map = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, T>>(json, options);
            if (parsed is null)
            {
                return false;
            }

            // Re-keyed rather than returned directly: the payload comes back with a default (ordinal)
            // comparer, and `COM3` / `com3` have to be one entry — see the note on PortMetadataService.
            foreach (var pair in parsed)
            {
                if (pair.Key is not null)
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int PickAllowed(int value, int[] allowed, int fallback)
    {
        foreach (var candidate in allowed)
        {
            if (candidate == value)
            {
                return value;
            }
        }

        return fallback;
    }
}
