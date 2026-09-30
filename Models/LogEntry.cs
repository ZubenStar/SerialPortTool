using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 日志条目模型 - 优化版本用于高性能日志处理
/// </summary>
public partial class LogEntry : ObservableObject
{
    /// <summary>
    /// 时间戳
    /// </summary>
    [ObservableProperty]
    private DateTime _timestamp = DateTime.Now;

    /// <summary>
    /// 串口名称
    /// </summary>
    [ObservableProperty]
    private string _portName = string.Empty;

    /// <summary>
    /// 日志内容
    /// </summary>
    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>
    /// 原始字节数据
    /// </summary>
    [ObservableProperty]
    private byte[]? _rawData;

    // `Format` (DataFormat) used to live here. Nothing read it — not the display path, not the file
    // logger, not the filters — and the only other consumer of DataFormat was CommandPreset, itself
    // unused. Both are gone rather than left as an implicit "the app supports formats" contract that
    // no code honours.

    /// <summary>
    /// 这条行代表什么：接收数据 / 发送数据 / 连接事件。
    /// </summary>
    /// <remarks>
    /// The full three-way answer, and the stored one. <c>IsReceived</c> used to be the field, which left no
    /// way to express a row about the connection itself — see <see cref="Core.Enums.LogEntryKind.Event"/>.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReceived))]
    [NotifyPropertyChangedFor(nameof(Direction))]
    private LogEntryKind _kind = LogEntryKind.Received;

    /// <summary>
    /// 是否为接收数据。
    /// </summary>
    /// <remarks>
    /// A derived view of <see cref="Kind"/>, kept because "is this a received row?" is the question the two
    /// remaining call sites actually ask (the port-colour sweep and the file writer). Read-only on purpose:
    /// it cannot express <see cref="LogEntryKind.Event"/>, so a setter would be a second way to describe the
    /// same field, and one that can silently disagree with it.
    /// </remarks>
    public bool IsReceived => Kind == LogEntryKind.Received;

    /// <summary>
    /// 这一行写进文本时的类别标记：<c>RX</c> / <c>TX</c> / <c>SYS</c>。
    /// </summary>
    /// <remarks>
    /// Shared with <c>FileLoggerService</c> rather than spelled out again there. The two used to be
    /// independent expressions of the same idea, and the file writer's copy was written before the third kind
    /// existed, so it would have labelled every event row <c>TX</c>.
    /// </remarks>
    public string Direction => Kind switch
    {
        LogEntryKind.Sent => "TX",
        LogEntryKind.Event => "SYS",
        _ => "RX",
    };

    /// <summary>
    /// Hex the row is painted with (already resolved for the active appearance by the ViewModel).
    /// </summary>
    /// <remarks>
    /// Empty by default on purpose: every construction site assigns a colour, so anything that ends up
    /// blank is a missed assignment that <c>HexColorToBrushConverter</c> surfaces as the themed default
    /// text brush instead of the old hard-coded black — which was invisible on the dark palette.
    /// </remarks>
    [ObservableProperty]
    private string _colorHex = string.Empty;

    // 缓存格式化文本以避免重复字符串分配
    private string? _cachedFormattedText;

    /// <summary>
    /// 格式化文本用于显示 (高性能访问,带缓存)
    /// </summary>
    public string FormattedText
    {
        get
        {
            if (_cachedFormattedText == null)
            {
                _cachedFormattedText = $"[{Timestamp:HH:mm:ss.fff}] [{PortName}] [{Direction}] {Content}";
            }
            return _cachedFormattedText;
        }
    }

    /// <summary>
    /// 转换为字符串表示
    /// </summary>
    public override string ToString()
        => $"[{Timestamp:HH:mm:ss.fff}] [{PortName}] [{Direction}] {Content}";

    /// <summary>
    /// 清除缓存的格式化文本(当属性变化时调用)
    /// </summary>
    partial void OnContentChanged(string value) => InvalidateCaches();
    partial void OnPortNameChanged(string value) => InvalidateCaches();
    partial void OnTimestampChanged(DateTime value) => InvalidateCaches();
    partial void OnKindChanged(LogEntryKind value) => InvalidateCaches();

    // ---- Highlight cache (v2.2.4) --------------------------------------------------------------
    // Keyword highlighting is per-row decoration, and rows are re-realized on every recycle, so the
    // regex work is cached here against the matcher's generation rather than repeated per
    // realization. The generation is what makes invalidation free: editing a rule bumps it, and every
    // entry is then stale on sight — no walk over the log buffer, no per-entry notification.
    //
    // A List rather than an array because the matcher produces it, and an *empty* list is a
    // meaningful cached answer ("this line matched nothing"); null means "not computed yet".

    /// <summary>Matcher generation these matches were computed against, or -1 when never computed.</summary>
    internal int HighlightGeneration { get; set; } = -1;

    /// <summary>Cached matches for <see cref="HighlightGeneration"/>, or null when not computed yet.</summary>
    internal List<HighlightMatch>? HighlightMatches { get; set; }

    /// <summary>
    /// Drops both caches. Every property that feeds <see cref="FormattedText"/> must call this: the
    /// highlight offsets are indices into that string, so a stale format silently paints the wrong
    /// characters — the same class of bug as a stale <c>FormattedText</c>, and just as invisible.
    /// </summary>
    private void InvalidateCaches()
    {
        _cachedFormattedText = null;
        HighlightGeneration = -1;
        HighlightMatches = null;
    }
}