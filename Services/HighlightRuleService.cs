using Microsoft.Extensions.Logging;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// JSON-backed highlight rules plus the compiled snapshot the log rows are decorated from.
/// </summary>
/// <remarks>
/// The rules live in one <c>settings.json</c> string key, the same way the quick-send library does:
/// they then inherit the settings service's atomic write and read-only protection instead of
/// re-implementing both, and need no migration story of their own.
/// </remarks>
public sealed class HighlightRuleService : IHighlightRuleService
{
    /// <summary>Settings key holding the serialized rule set.</summary>
    public const string RulesSettingKey = "HighlightRules";

    /// <summary>
    /// Match timeout for a compiled rule.
    /// </summary>
    /// <remarks>
    /// The same value the search box and <c>LogFilterService</c> use, and for the same reason: a
    /// user-authored pattern can backtrack catastrophically, and an unbounded match on a decoration
    /// feature is not an acceptable way to lose the UI thread.
    /// </remarks>
    public const int MatchTimeoutMs = 100;

    /// <summary>Most ranges painted on a single line.</summary>
    /// <remarks>
    /// A cap rather than a policy: a rule like <c>.</c> would otherwise produce one range per
    /// character, and each range becomes an object on the row. Eight distinct keywords on one line is
    /// already an unusual log line.
    /// </remarks>
    public const int MaxMatchesPerLine = 8;

    /// <summary>Longest line worth scanning.</summary>
    /// <remarks>
    /// Matches the truncation the receive path already applies (1000 characters) with headroom. A
    /// pasted multi-kilobyte line is not something a user reads keyword highlights on, and it is
    /// exactly the input a bad pattern backtracks on.
    /// </remarks>
    public const int MaxLineLength = 2000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<HighlightRuleService> _logger;

    /// <summary>Bumped once per compiled snapshot; what per-entry match caches are keyed against.</summary>
    private int _generation;

    public HighlightRuleService(ISettingsService settings, ILogger<HighlightRuleService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HighlightRule>> LoadAsync()
    {
        string raw;
        try
        {
            raw = await _settings.LoadSettingAsync(RulesSettingKey, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the highlight rule setting");
            return Array.Empty<HighlightRule>();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<HighlightRule>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<HighlightRule>>(raw, JsonOptions);
            return parsed is null ? Array.Empty<HighlightRule>() : Normalize(parsed);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The highlight rules could not be parsed; starting with none");
            return Array.Empty<HighlightRule>();
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<HighlightRule> rules)
        => _settings.SaveSettingAsync(RulesSettingKey, JsonSerializer.Serialize(rules, JsonOptions));

    /// <inheritdoc />
    public string ResolveColorHex(string slotHex, bool isDark) => PortColorPalette.Resolve(slotHex, isDark);

    /// <inheritdoc />
    public string? Validate(HighlightRule rule)
    {
        if (string.IsNullOrEmpty(rule.Pattern))
        {
            return "匹配内容不能为空";
        }

        if (!rule.IsRegex)
        {
            return null;
        }

        try
        {
            var regex = BuildRegex(rule);
            if (regex.IsMatch(string.Empty))
            {
                // Not a hard failure, but a rule matching the empty string produces no visible range
                // (zero-length matches are dropped), so it would look like a rule that silently does
                // nothing. Better to say so here than to let the user hunt for it.
                return "该正则能匹配空字符串，不会产生任何高亮，请加上具体字符";
            }
        }
        catch (ArgumentException ex)
        {
            return $"正则无效：{ex.Message}";
        }

        return null;
    }

    /// <inheritdoc />
    public IHighlightMatcher CreateMatcher(IReadOnlyList<HighlightRule> rules, bool isDark)
    {
        var regexes = new List<Regex>();
        var literals = new List<string>();
        var caseSensitive = new List<bool>();
        var colors = new List<string>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern))
            {
                continue;
            }

            var color = string.IsNullOrEmpty(rule.ColorHex)
                ? PortColorPalette.Slots[0].SlotHex
                : rule.ColorHex;

            if (rule.IsRegex)
            {
                try
                {
                    regexes.Add(BuildRegex(rule));
                }
                catch (ArgumentException ex)
                {
                    // A hand-edited settings.json can hold a pattern the edit dialog would have
                    // rejected. Skipping it keeps the rest of the rules working — and the warning is
                    // what makes it diagnosable, since the symptom is otherwise "one rule does nothing".
                    _logger.LogWarning(ex, "Skipping highlight rule {RuleId}: the pattern does not compile", rule.Id);
                    continue;
                }

                literals.Add(string.Empty);
                caseSensitive.Add(rule.IsCaseSensitive);
                colors.Add(ResolveColorHex(color, isDark));
            }
            else
            {
                regexes.Add(null!);
                literals.Add(rule.Pattern);
                caseSensitive.Add(rule.IsCaseSensitive);
                colors.Add(ResolveColorHex(color, isDark));
            }
        }

        return new Matcher(Interlocked.Increment(ref _generation), regexes, literals, caseSensitive, colors);
    }

    /// <summary>Compiles one rule's pattern with the shared timeout and the rule's case rule.</summary>
    private static Regex BuildRegex(HighlightRule rule)
    {
        var options = RegexOptions.CultureInvariant;
        if (!rule.IsCaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        return new Regex(rule.Pattern, options, TimeSpan.FromMilliseconds(MatchTimeoutMs));
    }

    /// <summary>Drops unusable rules and repairs identities, so callers never null-check.</summary>
    private static IReadOnlyList<HighlightRule> Normalize(IEnumerable<HighlightRule> rules)
    {
        var result = new List<HighlightRule>();

        foreach (var rule in rules)
        {
            if (rule is null || string.IsNullOrEmpty(rule.Pattern))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
            }

            result.Add(rule);
        }

        return result;
    }

    /// <summary>
    /// Compiled, immutable rule snapshot.
    /// </summary>
    /// <remarks>
    /// Three parallel arrays rather than a list of objects: a literal rule needs no <see cref="Regex"/>
    /// at all (a plain <c>IndexOf</c> is both faster and allocation-free), and keeping one slot per
    /// rule means a match only has to carry an index.
    /// </remarks>
    private sealed class Matcher : IHighlightMatcher
    {
        private static readonly IReadOnlyList<HighlightMatch> NoMatches = Array.Empty<HighlightMatch>();

        private readonly Regex?[] _regexes;
        private readonly string[] _literals;
        private readonly bool[] _caseSensitive;
        private readonly string[] _colors;

        public Matcher(
            int generation,
            List<Regex> regexes,
            List<string> literals,
            List<bool> caseSensitive,
            List<string> colors)
        {
            Generation = generation;
            _regexes = regexes.ToArray();
            _literals = literals.ToArray();
            _caseSensitive = caseSensitive.ToArray();
            _colors = colors.ToArray();
        }

        public int Generation { get; }

        public bool IsEmpty => _regexes.Length == 0;

        public string ColorHexFor(int ruleIndex) => _colors[ruleIndex];

        public IReadOnlyList<HighlightMatch> Match(string text)
        {
            if (IsEmpty || string.IsNullOrEmpty(text) || text.Length > MaxLineLength)
            {
                return NoMatches;
            }

            var found = new List<HighlightMatch>();

            for (var ruleIndex = 0; ruleIndex < _regexes.Length; ruleIndex++)
            {
                var regex = _regexes[ruleIndex];
                if (regex is null)
                {
                    CollectLiteral(text, _literals[ruleIndex], _caseSensitive[ruleIndex], ruleIndex, found);
                }
                else
                {
                    CollectRegex(text, regex, ruleIndex, found);
                }

                if (found.Count >= MaxMatchesPerLine)
                {
                    break;
                }
            }

            if (found.Count == 0)
            {
                // Deliberately not NoMatches: the caller caches per entry, and a shared empty instance
                // is fine to hand back but an empty *list* is what the cache documents holding. Both
                // work; returning the shared instance avoids one allocation on the common "no match" case.
                return NoMatches;
            }

            return found.Count == 1 ? found : ResolveOverlaps(found);
        }

        /// <summary>Collects literal (non-regex) occurrences of one rule's pattern.</summary>
        private static void CollectLiteral(
            string text,
            string literal,
            bool caseSensitive,
            int ruleIndex,
            List<HighlightMatch> found)
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var offset = 0;

            while (offset <= text.Length - literal.Length)
            {
                var index = text.IndexOf(literal, offset, comparison);
                if (index < 0)
                {
                    return;
                }

                found.Add(new HighlightMatch(index, literal.Length, ruleIndex));
                if (found.Count >= MaxMatchesPerLine)
                {
                    return;
                }

                // Advance past the match rather than by one: overlapping occurrences of the same
                // literal cannot both be painted anyway.
                offset = index + literal.Length;
            }
        }

        /// <summary>Collects regex matches for one rule.</summary>
        private static void CollectRegex(string text, Regex regex, int ruleIndex, List<HighlightMatch> found)
        {
            // EnumerateMatches walks without materialising a Match per hit; the rule's own timeout is
            // honoured either way.
            foreach (var match in regex.EnumerateMatches(text))
            {
                if (match.Length == 0)
                {
                    // A zero-length range paints nothing and would be a silent no-op.
                    continue;
                }

                found.Add(new HighlightMatch(match.Index, match.Length, ruleIndex));

                if (found.Count >= MaxMatchesPerLine)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Sorts by position and drops anything overlapping an earlier accepted range.
        /// </summary>
        /// <remarks>
        /// Earlier rules win: <c>TextHighlighter</c> ranges that overlap are ambiguous, and "the rule
        /// you put first takes precedence" is the only ordering a user can predict. The offset sort
        /// is stable enough here because ties are broken by rule index, which is the array order.
        /// </remarks>
        private static IReadOnlyList<HighlightMatch> ResolveOverlaps(List<HighlightMatch> found)
        {
            found.Sort(static (left, right) =>
            {
                var byStart = left.Start.CompareTo(right.Start);
                return byStart != 0 ? byStart : left.RuleIndex.CompareTo(right.RuleIndex);
            });

            var accepted = new List<HighlightMatch>(found.Count);
            var consumedUpTo = -1;

            foreach (var match in found)
            {
                if (match.Start < consumedUpTo)
                {
                    continue;
                }

                accepted.Add(match);
                consumedUpTo = match.Start + match.Length;
            }

            return accepted;
        }
    }
}
