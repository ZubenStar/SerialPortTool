namespace SerialPortTool.Models;

/// <summary>What activating a palette row does. The window, not the control, decides how.</summary>
public enum PaletteEntryKind
{
    /// <summary>A saved quick-send command. Activating it sends it.</summary>
    Snippet,

    /// <summary>An already-open port. Activating it closes it, mirroring the sidebar.</summary>
    OpenPort,

    /// <summary>A detected but unopened port. Activating it opens it.</summary>
    AvailablePort,
}

/// <summary>
/// One row in the command palette.
/// </summary>
/// <remarks>
/// <para>
/// A UI projection rather than a domain model — like <see cref="NotificationItem"/>, and for the same
/// reason. The palette lists three things that have nothing in common in the domain (a saved payload, an
/// open <c>PortViewModel</c>, a bare port name), and projecting them onto one shape is what lets the
/// result list be a single flat <c>ListView</c> with one template instead of three of everything.
/// </para>
/// <para>
/// Immutable and built fresh on every open, so there is no notification machinery to get wrong: the
/// palette is a snapshot of the app at the moment F2 was pressed.
/// </para>
/// </remarks>
public sealed class PaletteEntry
{
    public PaletteEntryKind Kind { get; init; }

    /// <summary>Primary text: the snippet's label, or the port name.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Secondary text: the snippet's group, or the port's state.</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Leading icon glyph.</summary>
    public string Glyph { get; init; } = string.Empty;

    /// <summary>Port identity colour, or empty for a snippet. Paints the channel bar.</summary>
    public string AccentHex { get; init; } = string.Empty;

    /// <summary>
    /// Everything the query is matched against.
    /// </summary>
    /// <remarks>
    /// Includes the snippet's <b>payload</b>, not just its label: recalling a command by a fragment of
    /// what it sends is the normal way people look for one. Composed once here so the per-keystroke
    /// filter stays a single <c>Contains</c> per row instead of rebuilding a composite string on every
    /// pass.
    /// </remarks>
    public string Keywords { get; init; } = string.Empty;

    /// <summary>The snippet to send, for <see cref="PaletteEntryKind.Snippet"/>.</summary>
    public SendSnippet? Snippet { get; init; }

    /// <summary>The port name, for the two port kinds.</summary>
    public string PortName { get; init; } = string.Empty;
}
