using System;
using System.Collections.Generic;
using Windows.UI;

namespace SerialPortTool.Models;

/// <summary>
/// One entry of the port identity palette.
/// </summary>
/// <remarks>
/// Only <see cref="SlotHex"/> is ever persisted (<c>settings.json</c> keys <c>PortColor_&lt;port&gt;</c>,
/// <c>TxColorHex</c>, <c>RxColorHex</c>) — that is what keeps the 10 colours stable and keeps old
/// settings files working across this change. Rendering always goes through
/// <see cref="Resolve(string, bool)"/>, which maps a stored slot to the variant that is legible on the
/// active appearance. Never persist a resolved (dark) hex.
/// </remarks>
public sealed class PortColorSlot
{
    public PortColorSlot(string slotHex, string darkHex, string name)
    {
        SlotHex = slotHex;
        DarkHex = darkHex;
        Name = name;
    }

    /// <summary>Light-theme hex. Also the persisted identity of the slot.</summary>
    public string SlotHex { get; }

    /// <summary>Dark-theme variant: same hue, lifted and slightly desaturated for #101317.</summary>
    public string DarkHex { get; }

    /// <summary>Chinese display name shown in the port-colour menu.</summary>
    public string Name { get; }

    /// <summary>Hex to render under the given appearance. Never persisted.</summary>
    public string Resolve(bool isDark) => isDark ? DarkHex : SlotHex;
}

/// <summary>
/// The port identity palette.
/// </summary>
/// <remarks>
/// The same ten hue slots are duplicated as <c>AppPortColor1Brush</c>…<c>AppPortColor10Brush</c> in
/// <c>Themes/Tokens.xaml</c>, because those brushes back the static swatches of the port-colour
/// <c>MenuFlyout</c> (XAML cannot data-bind a flyout that is declared once per list item). Whenever a
/// value changes here, change the matching brush in Tokens.xaml in the same edit.
/// </remarks>
public static class PortColorPalette
{
    public static readonly IReadOnlyList<PortColorSlot> Slots = new[]
    {
        new PortColorSlot("#107C10", "#6CCB5F", "绿色"),
        new PortColorSlot("#0078D4", "#60CDFF", "蓝色"),
        new PortColorSlot("#E74856", "#FF7A86", "红色"),
        new PortColorSlot("#FF8C00", "#EF9A3D", "橙色"),
        new PortColorSlot("#881798", "#BE7BE8", "紫色"),
        new PortColorSlot("#00B7C3", "#4FD6DE", "青色"),
        new PortColorSlot("#C239B3", "#E87BDD", "粉色"),
        new PortColorSlot("#498205", "#B4D45E", "橄榄绿"),
        new PortColorSlot("#8764B8", "#B49BDE", "淡紫"),
        new PortColorSlot("#CA5010", "#E08050", "棕橙"),
    };

    /// <summary>Hexes offered for TX (sent) rows — a deliberately small subset of the palette.</summary>
    public static readonly IReadOnlyList<PortColorSlot> TxOptions = new[]
    {
        Slots[1], // 蓝色
        Slots[2], // 红色
        Slots[3], // 橙色
        Slots[4], // 紫色
    };

    /// <summary>Default RX (received) colour: the first slot.</summary>
    public static string DefaultRxHex => Slots[0].SlotHex;

    /// <summary>Default TX (sent) colour: the blue slot.</summary>
    public static string DefaultTxHex => Slots[1].SlotHex;

    // Both variants of every slot, so Resolve() is idempotent: feeding it an already-resolved hex
    // yields the same answer for the same theme instead of drifting out of the palette.
    private static readonly Dictionary<string, PortColorSlot> ByAnyHex = BuildLookup();

    private static Dictionary<string, PortColorSlot> BuildLookup()
    {
        var map = new Dictionary<string, PortColorSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in Slots)
        {
            map[slot.SlotHex] = slot;
            map[slot.DarkHex] = slot;
        }
        return map;
    }

    /// <summary>
    /// Maps a stored slot hex (or an already-resolved hex) to the variant for the active appearance.
    /// Unknown values — e.g. a hex hand-edited into settings.json — are returned unchanged so they
    /// still render instead of silently snapping to a palette colour.
    /// </summary>
    public static string Resolve(string? hex, bool isDark)
    {
        if (string.IsNullOrEmpty(hex))
        {
            return string.Empty;
        }

        return ByAnyHex.TryGetValue(hex, out var slot) ? slot.Resolve(isDark) : hex;
    }

    /// <summary>
    /// Parses <c>#RRGGBB</c> or <c>#AARRGGBB</c>. Shared with <c>HexColorToBrushConverter</c> so
    /// there is exactly one hex parser in the app.
    /// </summary>
    public static Color ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Color.FromArgb(0, 0, 0, 0);
        }

        var value = hex.TrimStart('#').AsSpan();
        if (value.Length != 6 && value.Length != 8)
        {
            return Color.FromArgb(0, 0, 0, 0);
        }

        byte a = 255;
        int offset = 0;
        if (value.Length == 8)
        {
            if (!byte.TryParse(value[..2], System.Globalization.NumberStyles.HexNumber, null, out a))
            {
                return Color.FromArgb(0, 0, 0, 0);
            }
            offset = 2;
        }

        if (!byte.TryParse(value.Slice(offset, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !byte.TryParse(value.Slice(offset + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !byte.TryParse(value.Slice(offset + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromArgb(0, 0, 0, 0);
        }

        return Color.FromArgb(a, r, g, b);
    }
}
