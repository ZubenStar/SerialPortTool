using System.Text;
using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 文本编码的解析与规范化。
/// </summary>
/// <remarks>
/// <para>
/// The GB18030 case is also the check that the code-page provider is actually reachable: the assembly is
/// part of <c>Microsoft.NETCore.App</c> (so no package reference is needed), but "the assembly is present"
/// and "GetEncoding succeeds" are different statements, and only the second one lets a GB18030 device be
/// read. If this test ever starts failing, the feature has silently degraded to UTF-8 rather than broken.
/// </para>
/// <para>
/// GB18030's code page is 54936. It is asserted numerically on purpose — a comparison against
/// <c>Encoding.UTF8</c> would pass even when the fallback has taken over, which is exactly the failure this
/// test exists to catch.
/// </para>
/// </remarks>
public sealed class SerialEncodingsTests
{
    private const int Gb18030CodePage = 54936;
    private const int Utf8CodePage = 65001;

    // ---- 名单与规范化 ------------------------------------------------------------------------

    [Fact]
    public void SupportedNames_OffersUtf8AndGb18030()
    {
        Assert.Contains(SerialEncodings.Utf8Name, SerialEncodings.SupportedNames);
        Assert.Contains(SerialEncodings.Gb18030Name, SerialEncodings.SupportedNames);
    }

    [Theory]
    [InlineData(null, "UTF-8")]
    [InlineData("", "UTF-8")]
    [InlineData("   ", "UTF-8")]
    [InlineData("gb18030", "GB18030")]
    [InlineData("  GB18030  ", "GB18030")]
    [InlineData("utf-8", "UTF-8")]
    [InlineData("nonsense", "UTF-8")]
    public void Normalize_ReturnsACanonicalName(string? input, string expected)
    {
        Assert.Equal(expected, SerialEncodings.Normalize(input));
    }

    [Theory]
    [InlineData("UTF-8", true)]
    [InlineData("utf-8", true)]
    [InlineData("GB18030", true)]
    [InlineData("gb18030", true)]
    [InlineData("ascii", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_ReportsTheStoredNameUsableOrNot(string? input, bool expected)
    {
        // The distinction exists so a caller can warn about "settings.json names something we do not have"
        // while Normalize stays silent and total.
        Assert.Equal(expected, SerialEncodings.IsSupported(input));
    }

    // ---- 解析 ------------------------------------------------------------------------------

    [Fact]
    public void Resolve_Gb18030_UsesTheChineseCodePage()
    {
        Assert.Equal(Gb18030CodePage, SerialEncodings.Resolve(SerialEncodings.Gb18030Name).CodePage);
    }

    [Fact]
    public void Resolve_Utf8_IsTheFrameworkInstance()
    {
        Assert.Same(Encoding.UTF8, SerialEncodings.Resolve(SerialEncodings.Utf8Name));
    }

    [Fact]
    public void Resolve_UnknownName_FallsBackToUtf8InsteadOfThrowing()
    {
        // This call happens on the receive path, so throwing is not an option.
        Assert.Equal(Utf8CodePage, SerialEncodings.Resolve("not-a-real-encoding").CodePage);
    }

    [Fact]
    public void Resolve_IsCachedPerName()
    {
        Assert.Same(
            SerialEncodings.Resolve(SerialEncodings.Gb18030Name),
            SerialEncodings.Resolve("gb18030"));
    }

    // ---- 行为 ------------------------------------------------------------------------------

    [Fact]
    public void Gb18030_RoundTripsChineseText()
    {
        const string text = "温度 25℃";

        var encoding = SerialEncodings.Resolve(SerialEncodings.Gb18030Name);
        var bytes = encoding.GetBytes(text);

        Assert.Equal(text, encoding.GetString(bytes));
    }

    [Fact]
    public void DecodingGb18030BytesAsUtf8_ProducesReplacementCharacters()
    {
        // The fact the validation fix is built on: without a per-port encoding, a GB18030 payload decodes to
        // noise, and the quality heuristics then judge it as garbage. Documented here so the reason for the
        // encoding-aware validation cannot be forgotten.
        var gb18030Bytes = SerialEncodings.Resolve(SerialEncodings.Gb18030Name).GetBytes("温度");

        Assert.Contains('\uFFFD', Encoding.UTF8.GetString(gb18030Bytes));
    }

    [Fact]
    public void DescribeAvailability_NamesTheResolvedCodePage()
    {
        // The startup-log line, which is also how a self-contained build is confirmed to have shipped the
        // provider. Asserted here so the format cannot drift into something unreadable.
        var description = SerialEncodings.DescribeAvailability();

        Assert.Contains(SerialEncodings.Utf8Name, description);
        Assert.Contains(Gb18030CodePage.ToString(), description);
    }
}
