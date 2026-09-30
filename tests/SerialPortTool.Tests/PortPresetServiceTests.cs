using Microsoft.Extensions.Logging.Abstractions;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 命名串口配置档案（F3）。
/// </summary>
/// <remarks>
/// The interesting assertions are the ones about what a preset is <em>allowed</em> to remember. A preset
/// that silently misses one field is worse than no preset: it looks applied while sending bytes the user
/// did not intend, and nothing in the UI says otherwise.
/// </remarks>
public sealed class PortPresetServiceTests
{
    private static PortPresetService CreateService(FakeSettingsService settings)
        => new(settings, NullLogger<PortPresetService>.Instance);

    [Fact]
    public async Task Load_NoSavedValue_YieldsAnEmptyList()
    {
        Assert.Empty(await CreateService(new FakeSettingsService()).LoadAsync());
    }

    [Fact]
    public async Task Load_CorruptValue_YieldsAnEmptyListInsteadOfThrowing()
    {
        var settings = new FakeSettingsService();
        settings.Seed(PortPresetService.LibrarySettingKey, "]not[");

        Assert.Empty(await CreateService(settings).LoadAsync());
    }

    [Fact]
    public async Task RoundTrip_KeepsTheWholeProfileIncludingTheLineEnding()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        var preset = service.Create(new SerialPortProfile
        {
            BaudRate = 460800,
            DataBits = 7,
            StopBits = 2,
            Parity = 2,
            Handshake = 2,
            TextEncodingName = SerialEncodings.Gb18030Name,
            LineEnding = 1,
        }, "调试台");

        await service.SaveAsync(new[] { preset });

        var loaded = await service.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("调试台", loaded[0].Name);
        Assert.Equal(460800, loaded[0].Profile.BaudRate);
        Assert.Equal(7, loaded[0].Profile.DataBits);
        Assert.Equal(2, loaded[0].Profile.StopBits);
        Assert.Equal(2, loaded[0].Profile.Parity);
        Assert.Equal(2, loaded[0].Profile.Handshake);
        Assert.Equal(SerialEncodings.Gb18030Name, loaded[0].Profile.TextEncodingName);
        // The terminator is a send-side setting, not a line parameter, so this is the field most likely to
        // be forgotten — and the one whose loss changes the frame.
        Assert.Equal(1, loaded[0].Profile.LineEnding);
    }

    [Fact]
    public async Task Load_SanitizesAHandEditedValueBeforeItCanReachOpen()
    {
        var settings = new FakeSettingsService();
        settings.Seed(
            PortPresetService.LibrarySettingKey,
            """[{"name":"坏数据","profile":{"baudRate":0,"stopBits":0,"dataBits":9}}]""");

        var loaded = await CreateService(settings).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal(115200, loaded[0].Profile.BaudRate);
        Assert.Equal(1, loaded[0].Profile.StopBits);
        Assert.Equal(8, loaded[0].Profile.DataBits);
    }

    [Fact]
    public async Task Load_DropsUnnamedEntries()
    {
        var settings = new FakeSettingsService();
        settings.Seed(
            PortPresetService.LibrarySettingKey,
            """[{"name":"  "},{"name":"有效"}]""");

        var loaded = await CreateService(settings).LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("有效", loaded[0].Name);
    }

    [Fact]
    public async Task Load_AssignsAnIdToAnEntryWithoutOne()
    {
        var settings = new FakeSettingsService();
        settings.Seed(PortPresetService.LibrarySettingKey, """[{"name":"没有 id"}]""");

        var loaded = await CreateService(settings).LoadAsync();

        Assert.Single(loaded);
        Assert.False(string.IsNullOrWhiteSpace(loaded[0].Id));
    }

    // ---- 名称校验 ---------------------------------------------------------------------------

    private static PortPreset Named(string name) => new() { Id = "x", Name = name };

    [Fact]
    public void Validate_RejectsABlankName()
    {
        var error = CreateService(new FakeSettingsService()).Validate(Named("  "), new List<PortPreset>());

        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_RejectsADuplicateIgnoringCase()
    {
        // Rows in a menu differing only by case are indistinguishable, which makes picking one a coin toss.
        var service = CreateService(new FakeSettingsService());
        var existing = new List<PortPreset> { Named("AT 命令") };

        Assert.NotNull(service.Validate(Named("at 命令"), existing));
    }

    [Fact]
    public void Validate_AllowsKeepingItsOwnName()
    {
        // Editing parameters without touching the name must not be rejected as a duplicate of itself.
        var service = CreateService(new FakeSettingsService());
        var existing = new List<PortPreset> { new() { Id = "same", Name = "AT 命令" } };

        Assert.Null(service.Validate(new PortPreset { Id = "same", Name = "AT 命令" }, existing, "same"));
    }

    [Fact]
    public void Validate_EnforcesThePresetCap()
    {
        var service = CreateService(new FakeSettingsService());
        var existing = new List<PortPreset>();
        for (var i = 0; i < PortPresetService.MaxPresets; i++)
        {
            existing.Add(new PortPreset { Id = $"id{i}", Name = $"预设 {i}" });
        }

        Assert.NotNull(service.Validate(Named("再添一个"), existing));
    }

    [Fact]
    public void Create_ClampsTheName()
    {
        var created = CreateService(new FakeSettingsService())
            .Create(new SerialPortProfile(), new string('长', 200));

        Assert.Equal(PortPreset.MaxNameLength, created.Name.Length);
        Assert.False(string.IsNullOrWhiteSpace(created.Id));
    }

    // ---- 保存方向的规范化（v2.5.3）------------------------------------------------------------
    //
    // Load and save now share one Normalize. Before that, a save wrote whatever it was handed and the
    // *next load* silently dropped the invalid entries — the preset disappeared between sessions with
    // nothing in between to explain it.

    [Fact]
    public async Task Save_DropsUnnamedAndNullEntries()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        await service.SaveAsync(new List<PortPreset>
        {
            new() { Id = "a", Name = "   " },
            null!,
            new() { Id = "b", Name = "有效" },
        });

        var loaded = await service.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("有效", loaded[0].Name);
    }

    [Fact]
    public async Task Save_TruncatesToTheCap()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        var tooMany = new List<PortPreset>();
        for (var i = 0; i < PortPresetService.MaxPresets + 3; i++)
        {
            tooMany.Add(new PortPreset { Id = $"id{i}", Name = $"预设 {i}" });
        }

        await service.SaveAsync(tooMany);

        Assert.Equal(PortPresetService.MaxPresets, (await service.LoadAsync()).Count);
    }

    [Fact]
    public async Task Save_DoesNotRewriteTheCallersList()
    {
        // SaveAsync receives the ViewModel's live list: normalising in place would edit the ids and names
        // the UI is still displaying, so Normalize returns fresh instances instead.
        var settings = new FakeSettingsService();
        var callerOwned = new List<PortPreset> { new() { Id = string.Empty, Name = "  未命名  " } };

        await CreateService(settings).SaveAsync(callerOwned);

        Assert.Empty(callerOwned[0].Id);
        Assert.Equal("  未命名  ", callerOwned[0].Name);
    }
}
