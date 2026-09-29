namespace SerialPortTool.Models;

/// <summary>
/// How hard the receive path is currently being pushed.
/// </summary>
/// <remarks>
/// Ordered by severity only for readability; the modes are not a scale, they are two independent
/// verdicts and <see cref="LongLine"/> wins the tie because a stream with no line terminator is the
/// pathological case (it is what forces <c>ExtractCompleteLines</c> to flush an over-long tail as a
/// single entry).
/// </remarks>
public enum LogPressureMode
{
    /// <summary>Ordinary traffic — the flush loop does its full work.</summary>
    Normal = 0,

    /// <summary>A flood: far more bytes/lines per window than the UI can present at full detail.</summary>
    LargeOutput = 1,

    /// <summary>One un-terminated line has grown past <c>OutputPressureService.LongLineChars</c>.</summary>
    LongLine = 2,
}

/// <summary>
/// An immutable reading of the current output pressure, produced by
/// <see cref="Services.IOutputPressureService"/> and consumed by the UI flush loop.
/// </summary>
/// <param name="Mode">The dominant verdict for the current window.</param>
/// <param name="WindowBytes">Bytes counted in the window at the time of the reading (diagnostics).</param>
/// <param name="WindowLines">Lines counted in the window at the time of the reading (diagnostics).</param>
/// <param name="UnbrokenTailChars">
/// Characters currently buffered without a line terminator — the live long-line measure.
/// </param>
/// <remarks>
/// A <c>readonly record struct</c> on purpose: this is produced on every UI flush, and a class would
/// put one more short-lived object on the UI thread's allocation path for no benefit.
/// </remarks>
public readonly record struct LogPressureSnapshot(
    LogPressureMode Mode,
    int WindowBytes,
    int WindowLines,
    int UnbrokenTailChars)
{
    /// <summary>
    /// <c>true</c> when at least one degraded mode is active — the single flag the flush loop
    /// branches on.
    /// </summary>
    public bool IsDegraded => Mode != LogPressureMode.Normal;
}
