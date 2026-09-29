using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// Measures how hard the receive path is being pushed, so the UI flush can degrade optional work
/// instead of falling behind.
/// </summary>
/// <remarks>
/// <para>
/// Borrowed from Netcatty's output-pressure detector
/// (<c>components/terminal/runtime/terminalOutputPressure.ts</c>) — see the 借鉴 Backlog section in
/// AGENTS.md. The principle that matters: under a flood the UI thread should do <em>less per tick</em>,
/// not try harder.
/// </para>
/// <para>
/// <b>This service must never affect data correctness.</b> It cannot drop, reorder or delay a log
/// entry, and it must not be consulted by anything on the receive path — the receive path only
/// reports into it. Its single consumer is the UI flush loop, which uses the reading to skip work
/// whose result the next flush would overwrite anyway (per-port statistics, traffic totals) and, in
/// later phases, keyword highlighting.
/// </para>
/// </remarks>
public interface IOutputPressureService
{
    /// <summary>
    /// Records one received chunk. Called on each port's serial read thread — one per port, so it can
    /// be entered concurrently and must stay lock-free and allocation-free.
    /// </summary>
    /// <param name="byteCount">Bytes in this chunk.</param>
    /// <param name="lineCount">
    /// Complete lines produced by this chunk. Used instead of the byte count for the flood verdict on
    /// purpose: lines are what the UI actually pays for (one match plus one list entry each).
    /// </param>
    /// <param name="unbrokenTailChars">
    /// Characters currently buffered for this port without a line terminator. Positional argument
    /// rather than a setter because the last chunk wins — there is nothing to accumulate.
    /// </param>
    void NoteIncoming(int byteCount, int lineCount, int unbrokenTailChars);

    /// <summary>
    /// The current reading. Read from the UI thread; never mutates state.
    /// </summary>
    LogPressureSnapshot Current { get; }

    /// <summary>
    /// Drops every counter and clears the flood verdict.
    /// </summary>
    /// <remarks>
    /// Called when the log buffer is cleared wholesale: after 清空 there is no stream in flight that
    /// the previous verdict still describes, and leaving it armed would keep the first flushes after
    /// the clear degraded for up to <c>OutputPressureService.QuietMs</c>.
    /// </remarks>
    void Reset();
}
