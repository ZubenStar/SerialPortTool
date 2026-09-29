using Microsoft.Extensions.Logging.Abstractions;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// The matching half of keyword highlighting.
/// </summary>
/// <remarks>
/// Worth testing precisely because it is invisible when it breaks: a rule that silently stops matching
/// looks identical to a rule the user typed wrong. The cases below are the ones that failed during
/// development or that a hand-edited <c>settings.json</c> can reach.
/// </remarks>
public sealed class HighlightRuleServiceTests
{
    private const string DefaultColor = "#E74856";

    private static (HighlightRuleService Service, FakeSettingsService Settings) Create()
    {
        var settings = new FakeSettingsService();
        return (new HighlightRuleService(settings, NullLogger<HighlightRuleService>.Instance), settings);
    }

    private static HighlightRule Rule(
        string pattern,
        bool isRegex = false,
        bool caseSensitive = false,
        bool enabled = true)
        => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Pattern = pattern,
            IsRegex = isRegex,
            IsCaseSensitive = caseSensitive,
            Enabled = enabled,
            ColorHex = DefaultColor,
        };

    // ---- persistence -------------------------------------------------------------------------

    [Fact]
    public async Task LoadAsync_WithNothingStored_YieldsNoRules()
    {
        var (service, _) = Create();

        Assert.Empty(await service.LoadAsync());
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsEveryField()
    {
        var (service, settings) = Create();
        var rule = Rule("ERROR", isRegex: true, caseSensitive: true);

        await service.SaveAsync(new[] { rule });
        var loaded = await service.LoadAsync();

        Assert.True(settings.Has(HighlightRuleService.RulesSettingKey));
        var only = Assert.Single(loaded);
        Assert.Equal("ERROR", only.Pattern);
        Assert.True(only.IsRegex);
        Assert.True(only.IsCaseSensitive);
        Assert.Equal(DefaultColor, only.ColorHex);
        Assert.Equal(rule.Id, only.Id);
    }

    [Fact]
    public async Task LoadAsync_WithCorruptJson_YieldsNoRulesInsteadOfThrowing()
    {
        var (service, settings) = Create();
        settings.Seed(HighlightRuleService.RulesSettingKey, "{not json");

        // A decoration feature must never be able to stop the app from starting.
        Assert.Empty(await service.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_RepairsAMissingIdAndDropsPatternlessRules()
    {
        var (service, settings) = Create();
        settings.Seed(
            HighlightRuleService.RulesSettingKey,
            """[{"Pattern":"OK"},{"Pattern":""},{"Pattern":"WARN"}]""");

        var loaded = await service.LoadAsync();

        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, rule => Assert.False(string.IsNullOrWhiteSpace(rule.Id)));
    }

    // ---- validation --------------------------------------------------------------------------

    [Fact]
    public void Validate_RejectsAnEmptyPattern()
    {
        var (service, _) = Create();

        Assert.NotNull(service.Validate(Rule(string.Empty)));
    }

    [Fact]
    public void Validate_AcceptsALiteralWithRegexMetacharacters()
    {
        var (service, _) = Create();

        // The point of literal mode: "(" is a character, not the start of a group.
        Assert.Null(service.Validate(Rule("RX(1)")));
    }

    [Fact]
    public void Validate_RejectsARegexThatMatchesTheEmptyString()
    {
        var (service, _) = Create();

        // 'a*' matches "" — it would produce zero-range matches that are dropped, so the rule would look
        // broken rather than invalid. Rejecting it at edit time is the whole reason Validate exists.
        Assert.NotNull(service.Validate(Rule("a*", isRegex: true)));
    }

    [Fact]
    public void Validate_RejectsABrokenRegex()
    {
        var (service, _) = Create();

        Assert.NotNull(service.Validate(Rule("(unclosed", isRegex: true)));
    }

    [Fact]
    public void Validate_AcceptsAUsableRegex()
    {
        var (service, _) = Create();

        Assert.Null(service.Validate(Rule(@"\d+ms", isRegex: true)));
    }

    // ---- matching ----------------------------------------------------------------------------

    [Fact]
    public void CreateMatcher_WithNoRules_IsEmptySoCallersCanSkipTheWork()
    {
        var (service, _) = Create();

        Assert.True(service.CreateMatcher(Array.Empty<HighlightRule>(), isDark: false).IsEmpty);
    }

    [Fact]
    public void CreateMatcher_SkipsDisabledAndPatternlessRules()
    {
        var (service, _) = Create();

        var matcher = service.CreateMatcher(
            new[] { Rule("ERROR", enabled: false), Rule(string.Empty), Rule("WARN") },
            isDark: false);

        Assert.False(matcher.IsEmpty);

        // Only WARN survived, so it is rule index 0 and it now matches the text the disabled rule used to.
        var only = Assert.Single(matcher.Match("ERROR WARN"));
        Assert.Equal(0, only.RuleIndex);
        Assert.Equal(6, only.Start);
        Assert.Equal(4, only.Length);
    }

    [Fact]
    public void Match_HonoursTheCaseRule()
    {
        var (service, _) = Create();

        var insensitive = service.CreateMatcher(new[] { Rule("error") }, isDark: false);
        var sensitive = service.CreateMatcher(new[] { Rule("error", caseSensitive: true) }, isDark: false);

        Assert.Single(insensitive.Match("ERROR"));
        Assert.Empty(sensitive.Match("ERROR"));
        Assert.Single(sensitive.Match("error"));
    }

    [Fact]
    public void Match_FindsEveryNonOverlappingOccurrence()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(new[] { Rule("aa") }, isDark: false);

        var matches = matcher.Match("aaaa");

        // Two, not three: overlapping occurrences cannot both be painted, so the scan advances past the
        // match instead of by one character.
        Assert.Equal(2, matches.Count);
        Assert.Equal(new[] { 0, 2 }, matches.Select(m => m.Start));
    }

    [Fact]
    public void Match_ReturnsMatchesInAscendingOrderAcrossRules()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(new[] { Rule("bb"), Rule("aa") }, isDark: false);

        var matches = matcher.Match("aabb");

        Assert.Equal(new[] { 0, 2 }, matches.Select(m => m.Start));
        Assert.Equal(new[] { 1, 0 }, matches.Select(m => m.RuleIndex));
    }

    [Fact]
    public void Match_GivesPrecedenceToTheEarlierRuleWhenRangesOverlap()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(
            new[] { Rule("abcdef"), Rule("cde") },
            isDark: false);

        // "the rule you put first wins" is the only ordering a user can predict.
        var only = Assert.Single(matcher.Match("xabcdefy"));
        Assert.Equal(0, only.RuleIndex);
        Assert.Equal(6, only.Length);
        Assert.Equal(1, only.Start);
    }

    [Fact]
    public void Match_CapsTheRangesPaintedOnOneLine()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(new[] { Rule("a") }, isDark: false);

        // "a" matched against a wall of 'a' would otherwise produce one range per character, and each
        // range becomes an object on the row.
        Assert.Equal(
            HighlightRuleService.MaxMatchesPerLine,
            matcher.Match(new string('a', 50)).Count);
    }

    [Fact]
    public void Match_SkipsLinesLongerThanTheLimit()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(new[] { Rule("ERROR") }, isDark: false);

        var overlong = new string('x', HighlightRuleService.MaxLineLength) + "ERROR";

        Assert.Empty(matcher.Match(overlong));
        Assert.Single(matcher.Match("ERROR"));
    }

    [Fact]
    public void Match_ReturnsAnEmptyListForAnEmptyLine()
    {
        var (service, _) = Create();
        var matcher = service.CreateMatcher(new[] { Rule("ERROR") }, isDark: false);

        Assert.Empty(matcher.Match(string.Empty));
    }

    [Fact]
    public void Match_IgnoresRegexConstructsThatProduceNoRange()
    {
        var (service, _) = Create();
        // Alternation with an empty branch: the regex is usable per Validate (it does not match "" as a
        // whole), but some of its successful matches are zero-length and must not become ranges.
        var matcher = service.CreateMatcher(new[] { Rule("(?:(?<x>)o)|ERROR", isRegex: true) }, isDark: false);

        var matches = matcher.Match("o ERROR");

        Assert.All(matches, match => Assert.True(match.Length > 0));
    }

    [Fact]
    public void ColorHexFor_ReturnsTheHexResolvedForTheActiveAppearance()
    {
        var (service, _) = Create();

        var light = service.CreateMatcher(new[] { Rule("ERROR") }, isDark: false);
        var dark = service.CreateMatcher(new[] { Rule("ERROR") }, isDark: true);

        Assert.Equal(PortColorPalette.Resolve(DefaultColor, false), light.ColorHexFor(0));
        Assert.Equal(PortColorPalette.Resolve(DefaultColor, true), dark.ColorHexFor(0));
    }

    [Fact]
    public void CreateMatcher_StampsANewGenerationEveryTime()
    {
        var (service, _) = Create();
        var rules = new[] { Rule("ERROR") };

        var first = service.CreateMatcher(rules, isDark: false);
        var second = service.CreateMatcher(rules, isDark: false);

        // The generation is what invalidates every per-entry match cache in the log view without walking
        // the buffer, so a recompile that reused a number would leave stale ranges painted on screen.
        Assert.NotEqual(first.Generation, second.Generation);
    }
}
