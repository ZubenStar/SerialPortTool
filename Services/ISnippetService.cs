using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// Persistence and payload expansion for the quick-send library.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately persistence-only plus one pure helper. The observable grouping the flyout binds to
/// lives on the ViewModel, so this service has no UI state to keep in sync and nothing that has to be
/// marshalled to the UI thread — which also makes it directly unit-testable.
/// </para>
/// <para>
/// The whole library round-trips through a <b>single</b> <c>settings.json</c> string key. That is not
/// a shortcut: <see cref="ISettingsService"/> only has <c>int</c> and <c>string</c> overloads, and the
/// documented rule is to extend the settings surface by serializing to a string rather than by
/// growing that interface.
/// </para>
/// </remarks>
public interface ISnippetService
{
    /// <summary>
    /// Reads the library. Never throws: an unreadable or corrupt value yields an empty library and a
    /// warning, because a broken convenience feature must not stop the app from starting.
    /// </summary>
    /// <remarks>
    /// Entries are normalised on the way out — blank payloads dropped, a missing <see cref="SendSnippet.Id"/>
    /// or label repaired — so callers can treat what they get as well-formed.
    /// </remarks>
    Task<IReadOnlyList<SendSnippet>> LoadAsync();

    /// <summary>Writes the whole library back. Order is preserved.</summary>
    Task SaveAsync(IReadOnlyList<SendSnippet> snippets);

    /// <summary>
    /// Expands the substitutions a snippet with <c>UseVariables</c> opted into.
    /// </summary>
    /// <remarks>
    /// <para>Tokens (case-insensitive, resolved against <paramref name="now"/>'s local time):</para>
    /// <list type="bullet">
    /// <item><c>${date}</c> → <c>yyyy-MM-dd</c></item>
    /// <item><c>${time}</c> → <c>HH:mm:ss</c></item>
    /// <item><c>${datetime}</c> → <c>yyyy-MM-dd HH:mm:ss</c></item>
    /// <item><c>${epoch}</c> → Unix seconds</item>
    /// <item><c>${epochms}</c> → Unix milliseconds</item>
    /// </list>
    /// <para>Escapes: <c>\r</c>, <c>\n</c>, <c>\t</c>, <c>\0</c> and <c>\\</c>.</para>
    /// <para>
    /// <b>Anything unrecognised is emitted verbatim</b> — an unknown <c>${...}</c> token, or a
    /// backslash followed by a character that is not an escape. A payload that legitimately contains
    /// those sequences must never be silently rewritten into something else; the user opted into
    /// interpretation, not into losing bytes.
    /// </para>
    /// </remarks>
    string ExpandVariables(string content, DateTimeOffset now);
}
