using System.Collections.Generic;

namespace SerialPortTool.Models;

/// <summary>
/// One saved command in the quick-send library.
/// </summary>
/// <remarks>
/// <para>
/// A plain mutable POCO on purpose: the library is serialized as a JSON array into a single
/// <c>settings.json</c> string value (see <c>ISnippetService</c>), and <c>System.Text.Json</c> needs
/// settable properties for that. It is deliberately **not</b> an <c>ObservableObject</c> — the library
/// is edited through the ViewModel, which re-projects its own observable groups after every change,
/// so per-snippet change notification would be a second, redundant update path.
/// </para>
/// <para>
/// <see cref="Id"/> is the stable identity and is never shown. The label is user text and can be
/// duplicated; the id is what any future import/export or command-palette binding should key on.
/// </para>
/// </remarks>
public sealed class SendSnippet
{
    /// <summary>Stable identity. Repaired on load when missing or blank.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Free-text group name; empty means ungrouped and is displayed under a default heading.
    /// </summary>
    /// <remarks>
    /// The groups are derived from this field rather than stored as their own list, so the library
    /// cannot end up with an empty group or a snippet pointing at a group that does not exist — the
    /// two failure modes a separate group table always eventually produces.
    /// </remarks>
    public string Group { get; set; } = string.Empty;

    /// <summary>Display name; what the quick-send list shows.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// The payload exactly as the user typed it. Nothing is expanded here — expansion is a send-time
    /// concern, so what is stored is always what was reviewed.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Send <see cref="Content"/> as hex instead of UTF-8 text.</summary>
    public bool IsHex { get; set; }

    /// <summary>
    /// Interpret <c>${...}</c> tokens and backslash escapes in <see cref="Content"/> when sending.
    /// </summary>
    /// <remarks>
    /// One switch for both kinds of substitution so the behaviour stays predictable: off sends the
    /// payload byte-for-byte as typed (which is what a snippet containing a literal <c>\n</c> needs),
    /// on interprets it. The token list lives on <c>ISnippetService.ExpandVariables</c> — unknown
    /// tokens are left verbatim rather than being dropped.
    /// </remarks>
    public bool UseVariables { get; set; }

    /// <summary>Ordering within a group; ties fall back to the label.</summary>
    public int Sort { get; set; }
}

/// <summary>
/// One heading of the quick-send flyout: a group name plus its snippets in display order.
/// </summary>
/// <remarks>
/// A flat projection built by the ViewModel, not persisted. A nested <c>ItemsControl</c> over these
/// (heading + items) is used instead of a <c>CollectionViewSource</c> with <c>IsSourceGrouped</c>
/// because the flyout is a handful of rows and the grouped view source would add a second, implicit
/// collection to keep in sync.
/// </remarks>
public sealed class SnippetGroup
{
    public SnippetGroup(string name, IReadOnlyList<SendSnippet> items)
    {
        Name = name;
        Items = items;
    }

    /// <summary>Heading text — the group name, or the ungrouped heading.</summary>
    public string Name { get; }

    /// <summary>Members, already ordered.</summary>
    public IReadOnlyList<SendSnippet> Items { get; }
}
