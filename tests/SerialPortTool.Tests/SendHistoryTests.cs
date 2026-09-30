using System.Collections.Generic;
using System.Linq;
using SerialPortTool.Helpers;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 发送历史的合并与持久化。
/// </summary>
/// <remarks>
/// The persistence half is the interesting one: the whole point of not reusing the search history's
/// <c>string.Join("|")</c> scheme is that a send payload is arbitrary text, and these cases are what that
/// claim has to survive.
/// </remarks>
public sealed class SendHistoryTests
{
    private static List<string> Merge(IReadOnlyList<string> current, string payload)
        => SendHistory.Merge(current, payload);

    // ---- 合并 ------------------------------------------------------------------------------

    [Fact]
    public void Merge_OnEmpty_StartsWithThePayload()
    {
        Assert.Equal(new[] { "AT+RESET" }, Merge(new List<string>(), "AT+RESET"));
    }

    [Fact]
    public void Merge_PutsTheNewestFirst()
    {
        var merged = Merge(new List<string> { "second" }, "third");

        Assert.Equal(new[] { "third", "second" }, merged);
    }

    [Fact]
    public void Merge_OfAnAlreadyKnownPayload_MovesItToTheFrontWithoutDuplicating()
    {
        var merged = Merge(new List<string> { "newest", "again", "oldest" }, "again");

        Assert.Equal(new[] { "again", "newest", "oldest" }, merged);
    }

    [Fact]
    public void Merge_IgnoresBlankPayloads()
    {
        // ↑ is meant to offer something to resend; an empty entry would just empty the box.
        Assert.Empty(Merge(new List<string>(), "   "));
        Assert.Empty(Merge(new List<string>(), string.Empty));
    }

    [Fact]
    public void Merge_DoesNotModifyTheInputList()
    {
        var current = new List<string> { "keep" };

        _ = Merge(current, "new");

        Assert.Equal(new[] { "keep" }, current);
    }

    [Fact]
    public void Merge_TrimsBeyondTheCap_OldestFirst()
    {
        var current = new List<string>();
        for (var i = 0; i < SendHistory.MaxEntries + 5; i++)
        {
            current = Merge(current, $"cmd-{i}");
        }

        Assert.Equal(SendHistory.MaxEntries, current.Count);
        Assert.Equal($"cmd-{SendHistory.MaxEntries + 4}", current[0]);
        Assert.DoesNotContain("cmd-0", current);
    }

    // ---- 持久化 ------------------------------------------------------------------------------

    [Fact]
    public void RoundTrip_PreservesPayloadsThatWouldBreakAPipeSeparatedFormat()
    {
        // The reason this stores JSON: every one of these contains the delimiter the search history uses.
        var payloads = new List<string> { "a|b", "line1\nline2", "quote\"inside", "温度|25℃" };

        Assert.True(SendHistory.TryDeserialize(SendHistory.Serialize(payloads), out var restored));
        Assert.Equal(payloads, restored);
    }

    [Fact]
    public void TryDeserialize_OnBlank_ReportsNothingStored()
    {
        Assert.False(SendHistory.TryDeserialize(null, out var fromNull));
        Assert.Empty(fromNull);

        Assert.False(SendHistory.TryDeserialize(string.Empty, out var fromEmpty));
        Assert.Empty(fromEmpty);
    }

    [Fact]
    public void TryDeserialize_OnCorruptJson_ReportsFailureInsteadOfThrowing()
    {
        Assert.False(SendHistory.TryDeserialize("{", out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryDeserialize_OnAnEmptyArray_SucceedsWithNoEntries()
    {
        // Distinguishable from a corrupt value on purpose: this one is "the list is empty", the caller logs
        // nothing for it.
        Assert.True(SendHistory.TryDeserialize("[]", out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryDeserialize_DropsEmptyEntriesAndAppliesTheCap()
    {
        var json = "[\"\", \"keep\", null, \"x\"]";

        Assert.True(SendHistory.TryDeserialize(json, out var entries));
        Assert.Equal(new[] { "keep", "x" }, entries);

        var tooMany = SendHistory.Serialize(Enumerable.Range(0, 50).Select(i => $"cmd-{i}").ToList());
        Assert.True(SendHistory.TryDeserialize(tooMany, out var capped));
        Assert.Equal(SendHistory.MaxEntries, capped.Count);
    }
}
