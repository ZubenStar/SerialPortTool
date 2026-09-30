using System;

namespace SerialPortTool.Helpers;

/// <summary>
/// 日志行字号的取值范围与步进。
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project. The bounds live here
/// rather than in the ViewModel because two different things need to agree on them: the setter that persists the
/// value, and the loader that reads a hand-edited <c>settings.json</c>. One of them clamping differently from
/// the other is exactly the kind of drift that shows up as "the setting did not stick".
/// </para>
/// <para>
/// <see cref="Default"/> is also the row height anchor in <c>LogListView</c>: the control scales its wheel step
/// and its "at the bottom" slack from the row height this size produces. Its own copy of the number is
/// deliberate (a control must not reference the ViewModel) and only has to be accurate to "about a row".
/// </para>
/// </remarks>
public static class LogFontScale
{
    /// <summary>下限：再小等宽字的细节就开始看不清了。</summary>
    public const double Min = 10.0;

    /// <summary>上限：再大一行就放不下一条完整日志了。</summary>
    public const double Max = 24.0;

    /// <summary>默认字号，也是行高锚点对应的字号。</summary>
    public const double Default = 13.0;

    /// <summary>把任意输入收进合法区间。</summary>
    public static double Clamp(double fontSize) => Math.Clamp(fontSize, Min, Max);

    /// <summary>
    /// 按步数调整字号。
    /// </summary>
    /// <remarks>
    /// Clamped rather than rejected: the gesture's natural unit is "one step", and a shortcut that silently stops
    /// responding at the limit reads as broken rather than as bounded.
    /// </remarks>
    public static double Adjust(double fontSize, int steps) => Clamp(fontSize + steps);
}
