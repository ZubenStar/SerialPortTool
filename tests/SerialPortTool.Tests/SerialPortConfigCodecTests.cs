using SerialPortTool.Helpers;
using SerialPortTool.Models;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 预设（F3）与启动恢复（F9）共用的参数校验与 JSON 解析。
/// </summary>
/// <remarks>
/// The scenarios here all start from the same place: a value that arrived from <c>settings.json</c>, which
/// is a hand-editable file. "垃圾进 → 合法出" is the whole contract, because the alternative is a value
/// reaching <c>SerialPort.Open</c> and taking the open down — and the open failing with a cryptic message
/// is very far from the thing the user actually did (typed <c>"stopbits": 0</c> into a JSON file).
/// </remarks>
public sealed class SerialPortConfigCodecTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    // ---- 取值域 ------------------------------------------------------------------------------

    [Fact]
    public void NormalizeStopBits_RejectsNone()
    {
        // StopBits.None is a member of the enum and SerialPort refuses it at open time, so a preset that
        // merely forgot to set the field must not become an open failure.
        Assert.Equal(1, SerialPortConfigCodec.NormalizeStopBits(0));
        Assert.Equal(1, SerialPortConfigCodec.NormalizeStopBits(99));
        Assert.Equal(2, SerialPortConfigCodec.NormalizeStopBits(2));
        Assert.Equal(3, SerialPortConfigCodec.NormalizeStopBits(3));
    }

    [Fact]
    public void NormalizeDataBits_KeepsOnlyWhatSerialPortAccepts()
    {
        Assert.Equal(8, SerialPortConfigCodec.NormalizeDataBits(8));
        Assert.Equal(5, SerialPortConfigCodec.NormalizeDataBits(5));
        Assert.Equal(8, SerialPortConfigCodec.NormalizeDataBits(4));
        Assert.Equal(8, SerialPortConfigCodec.NormalizeDataBits(9));
    }

    [Fact]
    public void NormalizeBaudRate_KeepsZeroOutOfRange()
    {
        // Zero baud throws ArgumentOutOfRangeException inside SerialPort, and it is the value a truncated
        // JSON object yields.
        Assert.Equal(SerialPortConfigCodec.DefaultBaudRate, SerialPortConfigCodec.NormalizeBaudRate(0));
        Assert.Equal(SerialPortConfigCodec.DefaultBaudRate, SerialPortConfigCodec.NormalizeBaudRate(-1));
        Assert.Equal(921600, SerialPortConfigCodec.NormalizeBaudRate(921600));
    }

    [Fact]
    public void NormalizeLineEnding_FallsBackToNone()
    {
        // None is the app-wide default, and appending a terminator the user never chose changes every frame.
        Assert.Equal(0, SerialPortConfigCodec.NormalizeLineEnding(77));
        Assert.Equal(3, SerialPortConfigCodec.NormalizeLineEnding(3));
    }

    // ---- Sanitize ---------------------------------------------------------------------------

    [Fact]
    public void Sanitize_NullProfile_YieldsADefault()
    {
        // Reachable: a preset whose nested "Profile" member is missing deserialises to null.
        var result = SerialPortConfigCodec.Sanitize(null);

        Assert.Equal(115200, result.BaudRate);
        Assert.Equal(8, result.DataBits);
        Assert.Equal(1, result.StopBits);
        Assert.Equal(0, result.Parity);
        Assert.Equal(0, result.Handshake);
        Assert.Equal(0, result.LineEnding);
    }

    [Fact]
    public void Sanitize_RepairsEveryFieldAtOnce()
    {
        var result = SerialPortConfigCodec.Sanitize(new SerialPortProfile
        {
            BaudRate = 0,
            DataBits = 7,
            StopBits = 400,
            Parity = -3,
            Handshake = 12,
            LineEnding = 42,
            TextEncodingName = "not-an-encoding",
        });

        Assert.Equal(115200, result.BaudRate);
        Assert.Equal(7, result.DataBits);
        Assert.Equal(1, result.StopBits);
        Assert.Equal(0, result.Parity);
        Assert.Equal(0, result.Handshake);
        Assert.Equal(0, result.LineEnding);
        Assert.Equal(SerialEncodings.Utf8Name, result.TextEncodingName);
    }

    [Fact]
    public void Sanitize_DoesNotMutateTheInput()
    {
        // The preset library is read into objects the ViewModel binds to; silently rewriting the user's
        // saved value while validating it would make "save" write back something they never chose.
        var input = new SerialPortProfile { StopBits = 0 };

        SerialPortConfigCodec.Sanitize(input);

        Assert.Equal(0, input.StopBits);
    }

    [Fact]
    public void Sanitize_NormalizesAnUnrecognisedEncodingRatherThanDroppingIt()
    {
        var result = SerialPortConfigCodec.Sanitize(new SerialPortProfile { TextEncodingName = "gb18030" });

        Assert.Equal(SerialEncodings.Gb18030Name, result.TextEncodingName);
    }

    // ---- TryParseList -----------------------------------------------------------------------

    [Fact]
    public void TryParseList_BlankIsNotAnError()
    {
        // "Nothing saved yet" and "the saved value is broken" are different situations and the caller
        // logs only the second.
        Assert.False(SerialPortConfigCodec.TryParseList<SerialPortProfile>(string.Empty, Options, out var items));
        Assert.Empty(items);

        Assert.False(SerialPortConfigCodec.TryParseList<SerialPortProfile>("   ", Options, out var blank));
        Assert.Empty(blank);

        Assert.False(SerialPortConfigCodec.TryParseList<SerialPortProfile>(null, Options, out var nothing));
        Assert.Empty(nothing);
    }

    [Fact]
    public void TryParseList_CorruptJsonReturnsFalseInsteadOfThrowing()
    {
        Assert.False(SerialPortConfigCodec.TryParseList<SerialPortProfile>("{not json", Options, out var items));
        Assert.Empty(items);
    }

    [Fact]
    public void TryParseList_WrongShapeReturnsFalseInsteadOfThrowing()
    {
        // A `null` JSON literal deserialises fine and yields null; a string where an array was expected is
        // rejected by the serializer. Neither may escape as an exception — the feature that owns the value
        // is a convenience one.
        Assert.False(SerialPortConfigCodec.TryParseList<SerialPortProfile>("null", Options, out var items));
        Assert.Empty(items);
    }

    [Fact]
    public void TryParseList_RoundTripsARealList()
    {
        var source = new List<SerialPortProfile>
        {
            new() { BaudRate = 9600, Parity = 2 },
            new() { BaudRate = 115200 },
        };

        SerialPortConfigCodec.TryParseList<SerialPortProfile>(
            JsonSerializer.Serialize(source, Options), Options, out var parsed);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(9600, parsed[0].BaudRate);
        Assert.Equal(2, parsed[0].Parity);
    }

    [Fact]
    public void TryParseList_IgnoresUnknownPropertiesFromANewerBuild()
    {
        // Written down rather than assumed: the settings file outlives the build that wrote it, and a
        // forward-compatible read is what makes an upgrade non-destructive.
        var parsedOk = SerialPortConfigCodec.TryParseList<SerialPortProfile>(
            """[{"baudRate":4800,"somethingFuture":"x"}]""", Options, out var items);

        Assert.True(parsedOk);
        Assert.Single(items);
        Assert.Equal(4800, items[0].BaudRate);
    }
}
