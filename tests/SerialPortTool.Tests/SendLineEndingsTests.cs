using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 发送文本时的行尾符与多行拆分。
/// </summary>
/// <remarks>
/// Worth testing because the failure is silent and irreversible: a wrong terminator rule changes the bytes
/// that reach a device, and the only evidence is the device misbehaving. The cases below are the ones where
/// the byte stream is not obvious from reading the code.
/// </remarks>
public sealed class SendLineEndingsTests
{
    // ---- 终止符 ------------------------------------------------------------------------------

    [Fact]
    public void Terminator_MapsEveryMember()
    {
        Assert.Equal(string.Empty, SendLineEndings.Terminator(SendLineEnding.None));
        Assert.Equal("\r", SendLineEndings.Terminator(SendLineEnding.Cr));
        Assert.Equal("\n", SendLineEndings.Terminator(SendLineEnding.Lf));
        Assert.Equal("\r\n", SendLineEndings.Terminator(SendLineEnding.CrLf));
    }

    // ---- 默认行为必须逐字节不变 ----------------------------------------------------------------

    [Fact]
    public void None_LeavesThePayloadExactlyAsTyped()
    {
        // Embedded newlines included: someone who pasted a block into a snippet and did not ask for
        // line endings must still get that block verbatim.
        var plan = SendLineEndings.CreatePlan("AT+RESET\r\nAT+INFO", SendLineEnding.None, splitMultiline: true);

        Assert.Equal("AT+RESET\r\nAT+INFO", plan.Payload);
        Assert.Equal(new[] { "AT+RESET\r\nAT+INFO" }, plan.DisplayLines);
    }

    [Fact]
    public void None_IgnoresTheSplitFlag()
    {
        var split = SendLineEndings.CreatePlan("a\nb", SendLineEnding.None, splitMultiline: true);
        var unsplit = SendLineEndings.CreatePlan("a\nb", SendLineEnding.None, splitMultiline: false);

        Assert.Equal(unsplit.Payload, split.Payload);
    }

    // ---- 拆分 ------------------------------------------------------------------------------

    [Fact]
    public void Split_TerminatesEveryLine()
    {
        var plan = SendLineEndings.CreatePlan("A\nB", SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("A\rB\r", plan.Payload);
        Assert.Equal(new[] { "A", "B" }, plan.DisplayLines);
    }

    [Fact]
    public void Split_NormalizesWindowsAndBareCarriageReturns()
    {
        Assert.Equal(
            "A\r\nB\r\n",
            SendLineEndings.CreatePlan("A\r\nB", SendLineEnding.CrLf, splitMultiline: true).Payload);

        Assert.Equal(
            "A\nB\n",
            SendLineEndings.CreatePlan("A\rB", SendLineEnding.Lf, splitMultiline: true).Payload);
    }

    [Fact]
    public void Split_DoesNotTurnATrailingNewlineIntoAnExtraEmptyLine()
    {
        var plan = SendLineEndings.CreatePlan("A\n", SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("A\r", plan.Payload);
        Assert.Equal(new[] { "A" }, plan.DisplayLines);
    }

    [Fact]
    public void Split_KeepsAnInteriorBlankLine()
    {
        // A blank line is meaningful to a device reading a script, so it must survive as one terminator.
        var plan = SendLineEndings.CreatePlan("A\n\nB", SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("A\r\rB\r", plan.Payload);
        Assert.Equal(new[] { "A", string.Empty, "B" }, plan.DisplayLines);
    }

    [Fact]
    public void Split_KeepsTrailingWhitespaceInsideALine()
    {
        var plan = SendLineEndings.CreatePlan("A  \nB", SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("A  \rB\r", plan.Payload);
    }

    // ---- 不拆分 ------------------------------------------------------------------------------

    [Fact]
    public void Unsplit_AddsOneTerminatorAtTheEnd()
    {
        var plan = SendLineEndings.CreatePlan("A\nB", SendLineEnding.Cr, splitMultiline: false);

        Assert.Equal("A\nB\r", plan.Payload);
        Assert.Equal(new[] { "A\nB" }, plan.DisplayLines);
    }

    // ---- 边界 ------------------------------------------------------------------------------

    [Fact]
    public void EmptyInput_WithATerminator_SendsJustTheTerminator()
    {
        // Reachable through a snippet whose payload is empty after expansion. Sending a bare terminator is
        // a coherent command ("press Enter"), which is better than sending nothing at all.
        var plan = SendLineEndings.CreatePlan(string.Empty, SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("\r", plan.Payload);
        Assert.Equal(new[] { string.Empty }, plan.DisplayLines);
    }

    [Fact]
    public void OnlyNewlines_WithATerminator_SendsOneTerminator()
    {
        var plan = SendLineEndings.CreatePlan("\n", SendLineEnding.Cr, splitMultiline: true);

        Assert.Equal("\r", plan.Payload);
        Assert.Equal(new[] { string.Empty }, plan.DisplayLines);
    }
}
