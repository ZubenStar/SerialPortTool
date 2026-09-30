using System;
using System.Collections.Generic;
using System.Text;

namespace SerialPortTool.Helpers;

/// <summary>
/// 命令面板（F2）的匹配与排序：分词、忽略分隔符、大小写不敏感。
/// </summary>
/// <remarks>
/// <para>
/// UI-free and dependency-free so it can be linked into the logic-layer test project. This is the
/// dependency-free half of Netcatty's <c>lib/searchMatcher.ts</c>: normalisation, tokenisation, a
/// separator-insensitive ("compact") pass and a tiered score. The pinyin channel that file also has is
/// deliberately not ported — C# has no BCL pinyin support, so it would mean shipping a character table or
/// taking a dependency, and this project's rule is neither.
/// </para>
/// <para>
/// The behaviour that matters in a Chinese session: a user who types <c>重置 计数</c>, <c>重置-计数</c> or
/// <c>chongzhi</c>… the first two are handled here; the third is exactly what the pinyin channel would have
/// bought and is the reason it is documented as a deliberate omission rather than an oversight.
/// </para>
/// </remarks>
public static class PaletteSearchMatcher
{
    // Score tiers. The gaps are wide so a later tier can never be confused with an earlier one; only the
    // ordering is meaningful, not the absolute numbers.
    private const int TitlePrefixScore = 400;
    private const int TitleContainsScore = 320;
    private const int KeywordsContainScore = 240;
    private const int CompactTitleScore = 200;
    private const int CompactKeywordsScore = 140;
    private const int AllTokensScore = 100;

    /// <summary>
    /// Scores how well one entry matches <paramref name="query"/>.
    /// </summary>
    /// <param name="query">What the user typed. Empty matches everything, at the lowest score.</param>
    /// <param name="title">The row's title — the text the user reads, so it outranks the keywords.</param>
    /// <param name="keywords">Everything else the row can be found by, concatenated.</param>
    /// <returns>The score, or <c>null</c> when the entry does not match.</returns>
    public static int? Score(string? query, string title, string? keywords)
    {
        var normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
        {
            // An empty query is "show me everything", not "match nothing".
            return 0;
        }

        var tokens = Tokenize(normalizedQuery);
        if (tokens.Count == 0)
        {
            // A query made only of separators ("-", "---") is not a usable search term. Without this it
            // would match nearly every row, so it is rejected rather than treated as a substring.
            return null;
        }

        var titleText = Normalize(title);
        var keywordsText = Normalize(keywords);

        if (titleText.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            return TitlePrefixScore;
        }

        if (titleText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return TitleContainsScore;
        }

        if (keywordsText.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            return KeywordsContainScore;
        }

        var compactQuery = Compact(normalizedQuery);
        if (compactQuery.Length > 0)
        {
            if (Compact(titleText).Contains(compactQuery, StringComparison.Ordinal))
            {
                return CompactTitleScore;
            }

            if (Compact(keywordsText).Contains(compactQuery, StringComparison.Ordinal))
            {
                return CompactKeywordsScore;
            }
        }

        // Multi-word queries: every token has to appear, in any order and in any field. The query's own
        // separators are already gone, which is what makes "prod api" find "prod-api-01".
        var haystack = titleText + " " + keywordsText;
        foreach (var token in tokens)
        {
            if (!haystack.Contains(token, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return AllTokensScore;
    }

    /// <summary>
    /// Case-folds and Unicode-normalises text for comparison.
    /// </summary>
    /// <remarks>
    /// NFKC so that a full-width <c>Ａ</c> typed by an IME and the ASCII <c>A</c> in the data are the same
    /// character. <c>ToLowerInvariant</c> rather than a culture-aware lower-case: the Turkish dotless-i
    /// rule would make the same query match different rows on different machines.
    /// </remarks>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            return text.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Normalize throws on unpaired surrogates, which a paste can produce. The raw text is still a
            // perfectly usable haystack, so fall back rather than losing the search.
            return text.Trim().ToLowerInvariant();
        }
    }

    /// <summary>Splits the query on anything that is not a letter or a digit.</summary>
    /// <remarks>
    /// "Not a letter or digit" rather than an explicit punctuation list: it covers ASCII punctuation,
    /// every Unicode dash variant, and CJK punctuation without having to enumerate them, and it treats a
    /// CJK ideograph as the letter it is.
    /// </remarks>
    private static List<string> Tokenize(string normalizedQuery)
    {
        var tokens = new List<string>(4);
        var builder = new StringBuilder();

        foreach (var c in normalizedQuery)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    /// <summary>The same text with every separator removed.</summary>
    private static string Compact(string normalizedText)
    {
        var builder = new StringBuilder(normalizedText.Length);

        foreach (var c in normalizedText)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
