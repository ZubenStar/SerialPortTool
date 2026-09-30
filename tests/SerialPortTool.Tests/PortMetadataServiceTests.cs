using Microsoft.Extensions.Logging.Abstractions;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using SerialPortTool.Services;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 每端口备注 / 标签 / 分组的持久化（F6）。
/// </summary>
/// <remarks>
/// The behaviours pinned here are the ones that would otherwise be discovered by losing data: a value that
/// round-trips under one spelling of the port name and not another, an entry that survives a nonexistent
/// port forever, and a corrupt setting that takes down something it has no business taking down.
/// </remarks>
public sealed class PortMetadataServiceTests
{
    private static PortMetadataService CreateService(FakeSettingsService settings)
        => new(settings, NullLogger<PortMetadataService>.Instance);

    [Fact]
    public async Task Load_NoSavedValue_YieldsAnEmptyMap()
    {
        var loaded = await CreateService(new FakeSettingsService()).LoadAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task Load_CorruptValue_YieldsAnEmptyMapInsteadOfThrowing()
    {
        var settings = new FakeSettingsService();
        settings.Seed(PortMetadataService.MetadataSettingKey, "{ this is not json");

        var loaded = await CreateService(settings).LoadAsync();

        Assert.Empty(loaded);
    }

    [Fact]
    public async Task RoundTrip_KeepsEveryField()
    {
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        await service.SaveAsync(new[]
        {
            new PortMetadata { PortName = "COM3", Notes = "左侧那台传感器", Tags = "温度, 现场", Group = "实验台" },
        });

        var loaded = await service.LoadAsync();

        Assert.Single(loaded);
        Assert.Equal("左侧那台传感器", loaded["COM3"].Notes);
        Assert.Equal("温度, 现场", loaded["COM3"].Tags);
        Assert.Equal("实验台", loaded["COM3"].Group);
    }

    [Fact]
    public async Task Load_KeysAreCaseInsensitive()
    {
        // `COM3` and `com3` are the same port, so a note written against one spelling must be found by the
        // other — otherwise the row silently loses its note depending on how the scan named it.
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        await service.SaveAsync(new[] { new PortMetadata { PortName = "COM3", Notes = "hello" } });

        var loaded = await service.LoadAsync();

        Assert.True(loaded.ContainsKey("com3"));
        Assert.Equal("hello", loaded["COM3"].Notes);
    }

    [Fact]
    public async Task Save_DropsEntriesTheUserEmptied()
    {
        // Closing the editor after clearing every field must forget the entry, not leave a blank shell
        // behind forever.
        var settings = new FakeSettingsService();
        var service = CreateService(settings);

        await service.SaveAsync(new[]
        {
            new PortMetadata { PortName = "COM3", Notes = "real" },
            new PortMetadata { PortName = "COM4" },
            new PortMetadata { PortName = "COM5", Notes = "   " },
        });

        var loaded = await service.LoadAsync();

        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey("COM3"));
    }

    [Fact]
    public void Sanitize_FlattensNewlinesAndClamps()
    {
        var result = CreateService(new FakeSettingsService()).Sanitize(new PortMetadata
        {
            PortName = "  COM3  ",
            Notes = "第一行\r\n第二行",
            Tags = new string('t', 500),
            Group = new string('g', 500),
        });

        Assert.Equal("COM3", result.PortName);
        Assert.Equal("第一行 第二行", result.Notes);
        Assert.Equal(PortMetadata.MaxTagsLength, result.Tags.Length);
        Assert.Equal(PortMetadata.MaxGroupLength, result.Group.Length);
    }

    [Fact]
    public async Task Load_NullsInTheArrayAreDropped()
    {
        var settings = new FakeSettingsService();
        settings.Seed(PortMetadataService.MetadataSettingKey, "[null,{\"portName\":\"COM3\"}]");

        var loaded = await CreateService(settings).LoadAsync();

        Assert.Equal(new[] { "COM3" }, loaded.Keys.ToArray());
    }

    // ---- 标签 ------------------------------------------------------------------------------

    [Fact]
    public void Split_AcceptsBothHalfWidthAndFullWidthSeparators()
    {
        // A Chinese session types the full-width comma without thinking about it.
        var tags = PortTags.Split("现场，温度, 备用");

        Assert.Equal(new[] { "现场", "温度", "备用" }, tags.ToArray());
    }

    [Fact]
    public void Split_DeduplicatesIgnoringCase()
    {
        Assert.Single(PortTags.Split("USB, usb, USB"));
    }

    [Fact]
    public void Split_CapsTheNumberOfTags()
    {
        Assert.Equal(PortTags.MaxTags, PortTags.Split("a,b,c,d,e,f,g,h").Count);
    }

    [Fact]
    public void Split_BlankYieldsNothing()
    {
        Assert.Empty(PortTags.Split("   "));
        Assert.Empty(PortTags.Split(null));
    }

    [Fact]
    public void Join_IsReadableRatherThanLossless()
    {
        Assert.Equal("a, b", PortTags.Join(new[] { "a", "b" }));
    }
}
