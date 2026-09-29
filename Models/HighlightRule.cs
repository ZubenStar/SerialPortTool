using System.Text.Json.Serialization;

namespace SerialPortTool.Models;

/// <summary>
/// One keyword-highlight rule: a pattern and the colour to paint the matches with.
/// </summary>
/// <remarks>
/// <para>
/// A plain mutable POCO, serialized as a JSON array into one <c>settings.json</c> string key — the
/// same shape and the same reasoning as <see cref="SendSnippet"/> (see <c>ISnippetService</c>).
/// </para>
/// <para>
/// <see cref="ColorHex"/> is a <b>slot</b> colour, not a rendered one: the light hex, exactly like
/// <see cref="PortColorSlot"/>. The dark variant is resolved by the palette at match time, so a rule
/// keeps meaning the same thing across an appearance switch instead of freezing the tone it was
/// authored in.
/// </para>
/// </remarks>
public sealed class HighlightRule
{
    /// <summary>Stable identity. Repaired on load when missing or blank.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Text or regex to look for, depending on <see cref="IsRegex"/>.
    /// </summary>
    /// <remarks>
    /// Literal (non-regex) matching is the default because it is what a serial log usually needs —
    /// <c>ERROR</c>, <c>OK</c>, <c>TIMEOUT</c> — and because it makes regex metacharacters literal
    /// instead of accidental syntax, the same reasoning the search box uses.
    /// </remarks>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>Treat <see cref="Pattern"/> as a regular expression.</summary>
    public bool IsRegex { get; set; }

    /// <summary>Case-sensitive matching. Off by default: serial logs are rarely consistent about case.</summary>
    public bool IsCaseSensitive { get; set; }

    /// <summary>Light-palette slot hex, e.g. <c>#E74856</c>. Never a rendered value.</summary>
    public string ColorHex { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="ColorHex"/> resolved for the active appearance, assigned by the ViewModel so the
    /// rule list can preview the colour that will actually be painted.
    /// </summary>
    /// <remarks>
    /// Deliberately not persisted: it is derived, and writing it back would freeze the tone a rule was
    /// last viewed in — the exact bug the slot/rendered split exists to prevent. Same pattern as
    /// <c>PortViewModel.DisplayColorHex</c>.
    /// </remarks>
    [JsonIgnore]
    public string RenderedColorHex { get; set; } = string.Empty;

    /// <summary>Disabled rules stay in the list but are not compiled into the matcher.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Evaluation order, and therefore match precedence when ranges overlap.</summary>
    public int Sort { get; set; }
}

/// <summary>
/// One matched range inside a rendered line: a start offset, a length, and which rule produced it.
/// </summary>
/// <remarks>
/// A <c>readonly record struct</c> because these are produced per row realization and stored in a
/// per-entry cache — a class would add an allocation per match per line for no benefit. It carries a
/// <b>rule index</b> rather than a colour: the view resolves the index to one cached brush per rule,
/// so a line with twenty matches of one rule still needs exactly one brush.
/// </remarks>
public readonly record struct HighlightMatch(int Start, int Length, int RuleIndex);
