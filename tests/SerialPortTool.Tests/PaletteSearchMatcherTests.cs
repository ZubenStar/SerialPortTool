using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 命令面板（F2）的匹配与排序。
/// </summary>
/// <remarks>
/// The matcher's whole job is to decide which row the user meant, so a wrong answer is invisible: the panel
/// still opens and still shows rows, just not the one that was asked for. These cases pin the ordering and
/// the separator handling that the whole rewrite exists for.
/// </remarks>
public sealed class PaletteSearchMatcherTests
{
    private static int? Score(string query, string title, string keywords = "")
        => PaletteSearchMatcher.Score(query, title, keywords);

    // ---- 空查询 ------------------------------------------------------------------------------

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.Equal(0, Score(string.Empty, "任意"));
        Assert.Equal(0, Score("   ", "任意"));
    }

    // ---- 纯分隔符查询不能匹配一切 --------------------------------------------------------------

    [Theory]
    [InlineData("-")]
    [InlineData("---")]
    [InlineData(" / ")]
    [InlineData("，。")]
    public void SeparatorOnlyQuery_MatchesNothing(string query)
    {
        // Otherwise "-" would match almost every row in the list and the panel would answer a typo with
        // everything it has.
        Assert.Null(Score(query, "prod-api-01", "prod api 01"));
    }

    // ---- 排序 ------------------------------------------------------------------------------

    [Fact]
    public void TitlePrefix_OutranksAKeywordsOnlyMatch()
    {
        var byTitle = Score("重置", "重置计数器", "重置计数器 工具");
        var byKeywords = Score("重置", "清空日志", "清空日志 重置");

        Assert.NotNull(byTitle);
        Assert.NotNull(byKeywords);
        Assert.True(byTitle > byKeywords);
    }

    [Fact]
    public void TitleContains_OutranksAKeywordsOnlyMatch()
    {
        var byTitle = Score("日志", "清空日志", "清空日志");
        var byKeywords = Score("日志", "清空", "清空 日志");

        Assert.NotNull(byTitle);
        Assert.NotNull(byKeywords);
        Assert.True(byTitle > byKeywords);
    }

    // ---- 分隔符不敏感 ------------------------------------------------------------------------

    [Fact]
    public void MultiWordQuery_IgnoresTheSeparatorsBetweenTheTokens()
    {
        // "prod api" has to find "prod-api-01": this is the case the plain Contains check could not do.
        Assert.NotNull(Score("prod api", "其他", "prod-api-01"));
    }

    [Fact]
    public void MultiWordQuery_MatchesRegardlessOfTokenOrder()
    {
        Assert.NotNull(Score("api prod", "其他", "prod-api-01"));
    }

    [Fact]
    public void CompactQuery_MatchesTextWithSeparatorsRemoved()
    {
        Assert.NotNull(Score("prodapi", "其他", "prod-api-01"));
    }

    [Fact]
    public void CompactQuery_DoesNotRescueAQueryWhoseTokensAreMissing()
    {
        Assert.Null(Score("prodzzz", "其他", "prod-api-01"));
    }

    [Fact]
    public void CjkQuery_MatchesWithASpaceInTheMiddle()
    {
        Assert.NotNull(Score("重置 计数", "重置计数器"));
    }

    // ---- 归一化 ------------------------------------------------------------------------------

    [Fact]
    public void FullWidthInput_MatchesTheAsciiSpelling()
    {
        // What an IME can produce: NFKC folds it onto the ASCII the data actually holds.
        Assert.NotNull(Score("ＡＢ", "ab-01"));
    }

    [Fact]
    public void CaseIsIgnored()
    {
        Assert.NotNull(Score("error", "ERROR 日志"));
    }

    [Fact]
    public void QueryLongerThanTheText_DoesNotMatch()
    {
        Assert.Null(Score("zzz", "abc", "def"));
    }

    [Fact]
    public void OneMissingToken_IsEnoughToReject()
    {
        Assert.Null(Score("prod api zzz", "其他", "prod-api-01"));
    }
}
