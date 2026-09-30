using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 连接事件的行文案，以及同一行重复时的折叠。
/// </summary>
/// <remarks>
/// The aggregation half is the part worth testing: it decides whether a frame-error storm shows up as one
/// readable row or as thirty a second, and the wrong answer is invisible until someone reads the log during an
/// incident. The text half is here because a state name that never got a label would surface as a raw enum
/// value in the middle of the data.
/// </remarks>
public sealed class LogEventTextTests
{
    // ---- 状态文案 ----------------------------------------------------------------------------

    [Theory]
    [InlineData(ConnectionState.Connected, "已连接")]
    [InlineData(ConnectionState.Disconnected, "已断开")]
    [InlineData(ConnectionState.Error, "连接错误")]
    public void DescribeStateChange_NamesEveryState(ConnectionState state, string expected)
    {
        Assert.Equal(expected, LogEventText.DescribeStateChange(state));
    }

    // ---- 错误文案 ----------------------------------------------------------------------------

    [Fact]
    public void DescribeError_KeepsTheDriverMessageVerbatim()
    {
        // The wording is the evidence when a port misbehaves, so it must not be translated or trimmed.
        Assert.Equal("错误：A device attached to the system is not functioning.",
            LogEventText.DescribeError("A device attached to the system is not functioning."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void DescribeError_WithoutAMessage_StillSaysSomething(string? message)
    {
        Assert.Equal("端口错误", LogEventText.DescribeError(message!));
    }

    // ---- 聚合键 ------------------------------------------------------------------------------

    [Fact]
    public void AggregateKey_SamePortAndMessage_IsTheSameKey()
    {
        var first = LogEventText.AggregateKey("COM3", "Frame error");
        var second = LogEventText.AggregateKey("COM3", "Frame error");

        Assert.Equal(first, second);
    }

    [Fact]
    public void AggregateKey_DifferentPort_IsADifferentKey()
    {
        // Two ports reporting the same driver error are two facts, not one.
        Assert.NotEqual(
            LogEventText.AggregateKey("COM3", "Frame error"),
            LogEventText.AggregateKey("COM5", "Frame error"));
    }

    [Fact]
    public void AggregateKey_DifferentMessage_IsADifferentKey()
    {
        Assert.NotEqual(
            LogEventText.AggregateKey("COM3", "Frame error"),
            LogEventText.AggregateKey("COM3", "Overrun error"));
    }

    [Fact]
    public void AggregateKey_DoesNotCollideAcrossFieldBoundaries()
    {
        // Port and message are joined with a unit separator precisely so that a message containing the port name
        // cannot build the same key as a different pair.
        Assert.NotEqual(
            LogEventText.AggregateKey("COM3", "COM5 x"),
            LogEventText.AggregateKey("COM3 COM5", "x"));
    }

    // ---- 计数后缀 ----------------------------------------------------------------------------

    [Fact]
    public void WithRepeatCount_StatesTheWindowAndTheCount()
    {
        Assert.Equal("错误：Frame error（最近 1 秒内重复 17 次）",
            LogEventText.WithRepeatCount("错误：Frame error", 17, 1));
    }

    // ---- 聚合窗口 ----------------------------------------------------------------------------

    [Fact]
    public void Window_FirstOccurrenceIsEmitted_RepeatsAreNot()
    {
        var window = new EventAggregateWindow();
        var key = LogEventText.AggregateKey("COM3", "Frame error");

        Assert.True(window.Record(key, "COM3", "错误：Frame error"));
        Assert.False(window.Record(key, "COM3", "错误：Frame error"));
        Assert.False(window.Record(key, "COM3", "错误：Frame error"));
    }

    [Fact]
    public void Window_KeysAreIndependent()
    {
        var window = new EventAggregateWindow();
        var frame = LogEventText.AggregateKey("COM3", "Frame error");
        var overrun = LogEventText.AggregateKey("COM3", "Overrun error");

        Assert.True(window.Record(frame, "COM3", "错误：Frame error"));
        // A different error is not a repeat of the first one, so it gets its own line.
        Assert.True(window.Record(overrun, "COM3", "错误：Overrun error"));
    }

    [Fact]
    public void Window_DrainReportsOnlyRepeats_InFirstSeenOrder_AndClears()
    {
        var window = new EventAggregateWindow();
        var frame = LogEventText.AggregateKey("COM3", "Frame error");
        var overrun = LogEventText.AggregateKey("COM3", "Overrun error");
        var quiet = LogEventText.AggregateKey("COM5", "Timeout");

        window.Record(frame, "COM3", "错误：Frame error");
        window.Record(overrun, "COM3", "错误：Overrun error");
        window.Record(quiet, "COM5", "错误：Timeout");
        window.Record(frame, "COM3", "错误：Frame error");
        window.Record(frame, "COM3", "错误：Frame error");
        window.Record(overrun, "COM3", "错误：Overrun error");

        var repeats = window.DrainRepeats();

        // One occurrence is not a repeat: its line has already been written, and a "重复 1 次" summary would be
        // pure noise.
        Assert.Equal(2, repeats.Count);
        Assert.Equal(("COM3", "错误：Frame error", 3), repeats[0]);
        Assert.Equal(("COM3", "错误：Overrun error", 2), repeats[1]);

        // Draining is what closes the window: a second drain has nothing left to say.
        Assert.False(window.HasPending);
        Assert.Empty(window.DrainRepeats());
    }

    [Fact]
    public void Window_StartsEmpty()
    {
        var window = new EventAggregateWindow();

        Assert.False(window.HasPending);
        Assert.Empty(window.DrainRepeats());
    }

    [Fact]
    public void Window_ReopensAfterADrain()
    {
        // The caller restarts its timer on the next occurrence, so the same key must be treated as "first" again.
        var window = new EventAggregateWindow();
        var key = LogEventText.AggregateKey("COM3", "Frame error");

        Assert.True(window.Record(key, "COM3", "错误：Frame error"));
        Assert.False(window.Record(key, "COM3", "错误：Frame error"));
        _ = window.DrainRepeats();

        Assert.True(window.Record(key, "COM3", "错误：Frame error"));
    }

    [Fact]
    public void Window_KeepsTheLineAndPortOfTheFirstOccurrence()
    {
        // The summary has to be attributable, and it is built from what was recorded first because that is the
        // line already sitting in the log.
        var window = new EventAggregateWindow();
        var key = LogEventText.AggregateKey("COM3", "Frame error");

        window.Record(key, "COM3", "错误：Frame error");
        window.Record(key, "COM3", "错误：Frame error");

        var repeats = window.DrainRepeats();

        var only = Assert.Single(repeats);
        Assert.Equal("COM3", only.PortName);
        Assert.Equal("错误：Frame error", only.Line);
        Assert.Equal(2, only.Count);
    }

    /// <summary>记录顺序不影响键的比较，但会决定汇总行的顺序（见上一个用例）。</summary>
    [Fact]
    public void Window_RepeatCountIsCumulative()
    {
        var window = new EventAggregateWindow();
        var key = "k";

        for (var i = 0; i < 9; i++)
        {
            window.Record(key, "COM3", "line");
        }

        var repeats = window.DrainRepeats();

        Assert.Equal(9, Assert.Single(repeats).Count);
    }
}
