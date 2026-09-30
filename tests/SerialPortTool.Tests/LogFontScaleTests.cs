using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 日志字号的取值范围与步进。
/// </summary>
/// <remarks>
/// Small, but it is the shared answer for two callers that must agree — the setter and the loader that reads a
/// hand-edited settings file. A file naming 4 or 400 has to land somewhere readable, and the gesture has to stop
/// at the same place the file is clamped to.
/// </remarks>
public sealed class LogFontScaleTests
{
    [Fact]
    public void Bounds_SurroundTheDefault()
    {
        Assert.True(LogFontScale.Min < LogFontScale.Default);
        Assert.True(LogFontScale.Default < LogFontScale.Max);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(4)]
    public void Clamp_LiftsAnythingBelowTheMinimum(double input)
    {
        Assert.Equal(LogFontScale.Min, LogFontScale.Clamp(input));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(25)]
    public void Clamp_CapsAnythingAboveTheMaximum(double input)
    {
        Assert.Equal(LogFontScale.Max, LogFontScale.Clamp(input));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(18.5)]
    [InlineData(24)]
    public void Clamp_LeavesAValueInsideTheRangeAlone(double input)
    {
        Assert.Equal(input, LogFontScale.Clamp(input));
    }

    [Fact]
    public void Adjust_MovesByWholeSteps()
    {
        Assert.Equal(14, LogFontScale.Adjust(13, 1));
        Assert.Equal(11, LogFontScale.Adjust(12, -1));
        Assert.Equal(15, LogFontScale.Adjust(13, 2));
    }

    [Fact]
    public void Adjust_SaturatesAtBothEnds()
    {
        // The gesture's unit is "one step" and it must keep responding at the limit — a shortcut that silently
        // stops moving reads as broken rather than as bounded.
        Assert.Equal(LogFontScale.Max, LogFontScale.Adjust(LogFontScale.Max, 1));
        Assert.Equal(LogFontScale.Max, LogFontScale.Adjust(LogFontScale.Max - 1, 5));
        Assert.Equal(LogFontScale.Min, LogFontScale.Adjust(LogFontScale.Min, -1));
        Assert.Equal(LogFontScale.Min, LogFontScale.Adjust(LogFontScale.Min + 1, -5));
    }

    [Fact]
    public void Adjust_TakesAStepOfZeroAsNoChange()
    {
        Assert.Equal(LogFontScale.Default, LogFontScale.Adjust(LogFontScale.Default, 0));
    }
}
