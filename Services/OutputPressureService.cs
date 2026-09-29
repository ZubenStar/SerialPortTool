using SerialPortTool.Models;
using System;
using System.Threading;

namespace SerialPortTool.Services;

/// <summary>
/// Lock-free implementation of <see cref="IOutputPressureService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> A tumbling measurement window of <see cref="WindowMs"/> accumulates the bytes and
/// lines reported since the last reset; when either crosses its threshold the flood verdict is armed
/// until <see cref="QuietMs"/> after the last heavy report. Deciding on the window alone would make
/// the verdict flap (a sustained flood resets the counters every window), which is exactly why the
/// verdict has its own quiet period — the same two-part structure Netcatty uses
/// (<c>largeOutput</c> + <c>largeOutputUntil</c>).
/// </para>
/// <para>
/// <b>Why no lock.</b> <see cref="NoteIncoming"/> sits on the per-chunk read path of every open port.
/// The window reset is guarded by a compare-exchange on the window start tick, so exactly one caller
/// rotates it and the rest only add. A caller that races past a rotation simply contributes to the new
/// window; the worst outcome is one window's worth of bytes counted on the wrong side of the boundary,
/// which for a flood verdict is irrelevant. Nothing here allocates.
/// </para>
/// <para>
/// <b>Clock.</b> <see cref="Environment.TickCount64"/> is monotonic (milliseconds since boot) and is
/// immune to wall-clock changes, unlike <c>DateTime.Now</c> — the same reasoning the log-flush and
/// pin-evaluation code already uses.
/// </para>
/// </remarks>
public sealed class OutputPressureService : IOutputPressureService
{
    /// <summary>Length of the measurement window, in milliseconds.</summary>
    public const int WindowMs = 100;

    /// <summary>Bytes arriving within one window that count as a flood (~160 KB/s sustained).</summary>
    public const int LargeOutputBytesPerWindow = 16 * 1024;

    /// <summary>Lines arriving within one window that count as a flood (~1500 lines/s).</summary>
    /// <remarks>
    /// Deliberately just under the flush loop's own ceiling
    /// (<c>MaxUiLogEntriesPerFlush</c> × <c>1000 / FlushIntervalMs</c> = 2000 lines/s), because that is
    /// where the queue starts to grow faster than the UI thread can drain it at full detail.
    /// </remarks>
    public const int LargeOutputLinesPerWindow = 150;

    /// <summary>Un-terminated tail length that counts as one pathologically long line.</summary>
    /// <remarks>
    /// Four times the 1000-character truncation applied downstream, so an ordinary long-ish line does
    /// not arm the mode — only a stream that never emits a terminator does.
    /// </remarks>
    public const int LongLineChars = 4096;

    /// <summary>How long the flood verdict stays armed after the last heavy window.</summary>
    /// <remarks>
    /// Roughly seven windows of quiet. Traffic is bursty (a burst, a pause, the next burst), and
    /// re-deciding every window would flap the degraded state on and off between flushes — each flap
    /// costs a statistics refresh the UI thread can ill afford mid-flood.
    /// </remarks>
    public const int QuietMs = 750;

    // ---- Written by the read threads, read by the UI thread. All access goes through Interlocked /
    // ---- Volatile so no lock is needed and no torn read is possible on 64-bit values.

    /// <summary>Tick the current measurement window started at.</summary>
    private long _windowStartTick;

    /// <summary>Bytes reported since the window started.</summary>
    private long _windowBytes;

    /// <summary>Lines reported since the window started.</summary>
    private long _windowLines;

    /// <summary>Tick until which the flood verdict stays armed; <c>0</c> when never armed.</summary>
    private long _largeOutputUntilTick;

    /// <summary>Characters currently buffered without a line terminator (last reporter wins).</summary>
    private int _unbrokenTailChars;

    public OutputPressureService()
    {
        _windowStartTick = Environment.TickCount64;
    }

    /// <inheritdoc />
    public void NoteIncoming(int byteCount, int lineCount, int unbrokenTailChars)
    {
        var now = Environment.TickCount64;

        // The tail is a snapshot, not an accumulator: whichever chunk reported last is the current
        // state of that port's line assembler. A plain volatile store is enough (and cheaper than an
        // interlocked exchange on the per-chunk path).
        Volatile.Write(ref _unbrokenTailChars, unbrokenTailChars);

        // Tumbling window: whoever wins the CAS zeroes the counters, everyone else just keeps adding.
        var windowStart = Volatile.Read(ref _windowStartTick);
        if (now - windowStart >= WindowMs &&
            Interlocked.CompareExchange(ref _windowStartTick, now, windowStart) == windowStart)
        {
            Interlocked.Exchange(ref _windowBytes, 0);
            Interlocked.Exchange(ref _windowLines, 0);
        }

        // Add() returns the post-add value, so a single burst can arm the verdict immediately instead
        // of waiting for the window to rotate.
        var bytes = Interlocked.Add(ref _windowBytes, byteCount);
        var lines = Interlocked.Add(ref _windowLines, lineCount);

        if (bytes >= LargeOutputBytesPerWindow || lines >= LargeOutputLinesPerWindow)
        {
            Volatile.Write(ref _largeOutputUntilTick, now + QuietMs);
        }
    }

    /// <inheritdoc />
    public LogPressureSnapshot Current
    {
        get
        {
            var now = Environment.TickCount64;
            var largeOutput = now < Volatile.Read(ref _largeOutputUntilTick);
            var tail = Volatile.Read(ref _unbrokenTailChars);
            var longLine = tail >= LongLineChars;

            var mode = longLine
                ? LogPressureMode.LongLine
                : largeOutput
                    ? LogPressureMode.LargeOutput
                    : LogPressureMode.Normal;

            return new LogPressureSnapshot(
                mode,
                ClampToInt(Volatile.Read(ref _windowBytes)),
                ClampToInt(Volatile.Read(ref _windowLines)),
                tail);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        Volatile.Write(ref _windowStartTick, Environment.TickCount64);
        Interlocked.Exchange(ref _windowBytes, 0);
        Interlocked.Exchange(ref _windowLines, 0);
        Volatile.Write(ref _largeOutputUntilTick, 0);
        Volatile.Write(ref _unbrokenTailChars, 0);
    }

    /// <summary>
    /// Saturating narrowing for the diagnostic counters. A pathological burst between two reads can
    /// exceed <see cref="int.MaxValue"/> only in theory; saturating keeps the reading meaningful
    /// instead of wrapping to a negative number.
    /// </summary>
    private static int ClampToInt(long value) =>
        value <= 0 ? 0 : value > int.MaxValue ? int.MaxValue : (int)value;
}
