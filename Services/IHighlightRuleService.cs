using SerialPortTool.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// Storage, validation and matching for the log keyword-highlight rules.
/// </summary>
/// <remarks>
/// <para>
/// Borrowed from Netcatty's keyword highlighting (<c>components/terminal/keywordHighlight.ts</c>),
/// with the part that matters most on this side of the fence kept intact: matching is <b>collapseable
/// under load</b>. The matcher is an immutable snapshot behind an integer generation, so the caller
/// can drop it entirely while the receive path is under pressure and pick the same snapshot back up
/// afterwards without rebuilding anything.
/// </para>
/// <para>
/// Everything here is UI-free: matches are returned as offsets plus a rule index, and colours as hex
/// strings. The view owns the brushes. That is what keeps this unit-testable without a XAML host.
/// </para>
/// </remarks>
public interface IHighlightRuleService
{
    /// <summary>
    /// Reads the rules. Never throws — an unreadable or corrupt value yields an empty rule set and a
    /// warning, because a decoration feature must not stop the app from starting.
    /// </summary>
    Task<IReadOnlyList<HighlightRule>> LoadAsync();

    /// <summary>Writes the whole rule set back. Order is preserved.</summary>
    Task SaveAsync(IReadOnlyList<HighlightRule> rules);

    /// <summary>
    /// Compiles a snapshot for the given appearance. Disabled and unusable rules are skipped, so the
    /// returned matcher is safe to run against every line.
    /// </summary>
    /// <param name="rules">The rule set, in evaluation order.</param>
    /// <param name="isDark">Which palette variant to resolve <see cref="HighlightRule.ColorHex"/> against.</param>
    IHighlightMatcher CreateMatcher(IReadOnlyList<HighlightRule> rules, bool isDark);

    /// <summary>
    /// Returns a user-facing error for a rule, or <c>null</c> when it is usable.
    /// </summary>
    /// <remarks>
    /// Checked at edit time so a broken pattern is rejected where it can be explained, rather than
    /// being silently skipped by <see cref="CreateMatcher"/> and looking like a broken feature.
    /// </remarks>
    string? Validate(HighlightRule rule);

    /// <summary>Resolves a slot hex to the rendered hex for the given appearance.</summary>
    string ResolveColorHex(string slotHex, bool isDark);
}

/// <summary>
/// An immutable, pre-compiled snapshot of the highlight rules.
/// </summary>
public interface IHighlightMatcher
{
    /// <summary>
    /// Identifies this snapshot. Callers cache matches per line against it, so a bump invalidates
    /// every cache at once without walking the log buffer.
    /// </summary>
    int Generation { get; }

    /// <summary>
    /// True when there is nothing to match (no rules, or every rule disabled/unusable). Lets callers
    /// skip the call entirely — which is the cheapest possible outcome on the per-row path.
    /// </summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Finds the highlightable ranges in one line, in ascending order and without overlaps.
    /// </summary>
    /// <remarks>
    /// Allocates only the returned list, and returns an empty list rather than <c>null</c> so callers
    /// can cache it as "matched nothing" without a separate flag.
    /// </remarks>
    IReadOnlyList<HighlightMatch> Match(string text);

    /// <summary>
    /// The rendered hex for a rule index from <see cref="HighlightMatch"/>.
    /// </summary>
    string ColorHexFor(int ruleIndex);
}
