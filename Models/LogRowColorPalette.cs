using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 日志行的配色：事件行（<see cref="LogEntryKind.Event"/>）用的中性灰，以及"按行类别取色"的入口。
/// </summary>
/// <remarks>
/// <para>
/// A flat hex pair rather than a <c>Themes/Tokens.xaml</c> entry, deliberately. A row colour reaches the
/// screen as a <em>hex string</em> through <c>LogEntry.ColorHex</c> → <c>HexColorToBrushConverter</c>, which
/// is what lets a theme switch re-colour rows that are already on screen; a token brush cannot take that
/// route. The same constraint is why the two values have to read on all three palettes including high
/// contrast — the converter has no way to reach the system text brush. That lesson is already recorded in
/// the converter's own fallback, which is a neutral grey for exactly this reason.
/// </para>
/// <para>
/// Grey on purpose: an event row must read as "not a channel", and every hue in
/// <see cref="PortColorPalette"/> is already spoken for by a port identity. The two variants differ only in
/// luminance, because a single grey cannot sit legibly on both #FFFFFF and #101317.
/// </para>
/// </remarks>
public static class LogRowColorPalette
{
    /// <summary>Event-row hex on the light palette.</summary>
    private const string EventRowLightHex = "#6E6E6E";

    /// <summary>Event-row hex on the dark palette — lifted so it does not disappear into #101317.</summary>
    private const string EventRowDarkHex = "#A6A6A6";

    /// <summary>Hex an event row is painted with under the given appearance.</summary>
    public static string EventRow(bool isDark) => isDark ? EventRowDarkHex : EventRowLightHex;

    /// <summary>
    /// 按行类别取该行在当前外观下应该用的颜色。
    /// </summary>
    /// <remarks>
    /// The appearance sweep must go through here rather than calling
    /// <see cref="PortColorPalette.Resolve"/> directly. That method returns a hex it does not recognise
    /// unchanged, and an event row's grey is not a palette slot — so the sweep would silently leave event
    /// rows on the previous appearance while every data row moved to the new one.
    /// </remarks>
    public static string Resolve(LogEntryKind kind, string currentHex, bool isDark)
        => kind == LogEntryKind.Event ? EventRow(isDark) : PortColorPalette.Resolve(currentHex, isDark);
}
