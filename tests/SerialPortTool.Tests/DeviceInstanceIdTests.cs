using SerialPortTool.Helpers;
using System.Collections.Generic;
using Xunit;

namespace SerialPortTool.Tests;

/// <summary>
/// 设备实例 id 的解析：序列号与 USB 位置的判定。
/// </summary>
/// <remarks>
/// <para>
/// 这里的数据来自一台真机上 <c>Get-PnpDevice -Class Ports</c> 的真实输出，不是构造出来的例子。这恰恰是这个
/// 启发式需要的验证方式：它取的是实例 id 里的某一段文本，"看着像序列号"与"真的是序列号"之间没有类型系统可
/// 依靠，只有真实形态能说明问题。
/// </para>
/// <para>
/// 回归那一条是这份测试存在的理由。FTDIBUS 在实例 id 末尾恒定加一段 <c>\0000</c>（设备对象序号），原实现取
/// "最后一段"，于是在一整台机器上返回同一个序列号 <c>0000</c>——一块四口板子的四个口、外加另一颗单口芯片全部
/// 显示相同的 SN。它看起来像个真值，实际没有任何区分能力，比留空更糟。
/// </para>
/// </remarks>
public sealed class DeviceInstanceIdTests
{
    /// <summary><c>GUID_DEVINTERFACE_COMPORT</c>：设备接口路径的尾部就是这一段。</summary>
    private const string ComPortInterfaceGuid = "{86e0d1e0-8089-11d0-9ce4-08003e301f73}";

    /// <summary>真机上那颗 FT232R（单口，芯片烧了序列号）的接口路径。</summary>
    private const string FtdiSinglePortInterfacePath =
        @"\\?\FTDIBUS#VID_0403+PID_6001+A50285BIA#0000#{" + ComPortInterfaceGuid + "}";

    /// <summary>真机上那颗 FT4232H（四口，没烧序列号）的接口路径前缀，后面拼口序号。</summary>
    private const string FtdiQuadPortInterfacePathPrefix =
        @"\\?\FTDIBUS#VID_0403+PID_6011+6&2F625D12&0&1&";

    // ---- 接口路径 → 实例 id ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\?\")]
    public void FromInterfacePath_NoUsableInput_YieldsNull(string? interfacePath)
    {
        Assert.Null(DeviceInstanceId.FromInterfacePath(interfacePath));
    }

    [Fact]
    public void FromInterfacePath_StripsThePrefixAndTheInterfaceGuid()
    {
        Assert.Equal(
            @"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000",
            DeviceInstanceId.FromInterfacePath(FtdiSinglePortInterfacePath));
    }

    [Theory]
    [InlineData(@"\\?\USB#VID_1A86&PID_7523#5&1b4e8a2c&0&1#{86e0d1e0-8089-11d0-9ce4-08003e301f73}",
        @"USB\VID_1A86&PID_7523\5&1b4e8a2c&0&1")]
    // 没有 GUID 段时也要能还原：GUID 段是按约定出现，不是靠约定存在。
    [InlineData(@"USB#VID_0403&PID_6001#A50285BI", @"USB\VID_0403&PID_6001\A50285BI")]
    // 本身就是实例 id 形态的输入原样通过（大小写在还原阶段不动）。
    [InlineData(@"USB\VID_0403&PID_6001\A50285BI", @"USB\VID_0403&PID_6001\A50285BI")]
    public void FromInterfacePath_ReturnsTheInstanceId(string interfacePath, string expected)
    {
        Assert.Equal(expected, DeviceInstanceId.FromInterfacePath(interfacePath));
    }

    // ---- VID / PID --------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"ACPI\PNP0501\1")]          // 板载串口没有 USB 标识符，不能凭空造一个
    [InlineData(@"USB\FTDI\A50285BI")]       // 有 VID/PID 段但格式不符时为 null，而不是截出一段数字
    public void ReadVendorIdAndProductId_WithoutUsbIdentifiers_YieldNull(string? instanceId)
    {
        Assert.Null(DeviceInstanceId.ReadVendorId(instanceId));
        Assert.Null(DeviceInstanceId.ReadProductId(instanceId));
    }

    [Fact]
    public void ReadVendorIdAndProductId_AreUppercased()
    {
        const string instanceId = @"ftdibus\vid_0403+pid_6001+a50285bia\0000";

        Assert.Equal("0403", DeviceInstanceId.ReadVendorId(instanceId));
        Assert.Equal("6001", DeviceInstanceId.ReadProductId(instanceId));
    }

    // ---- 序列号 -----------------------------------------------------------------------------

    [Fact]
    public void ReadSerialNumber_FtdibusWithABurnedSerial_ReturnsIt()
    {
        var serial = DeviceInstanceId.ReadSerialNumber(
            DeviceInstanceId.FromInterfacePath(FtdiSinglePortInterfacePath)!);

        Assert.Equal("A50285BIA", serial);
    }

    [Fact]
    public void ReadLocation_FtdibusWithABurnedSerial_YieldsNull()
    {
        var location = DeviceInstanceId.ReadLocation(
            DeviceInstanceId.FromInterfacePath(FtdiSinglePortInterfacePath)!);

        Assert.Null(location);
    }

    [Fact]
    public void ReadSerialNumber_FtdibusWithoutASerial_YieldsNullForEveryPortOfTheChip()
    {
        // 四个口共享同一颗芯片、同一个 USB 位置，只差末尾的口序号。它们都没有序列号，所以 SN 必须全部为空 ——
        // 这正是修复前显示成四个 "0000" 的那一组。
        for (var port = 1; port <= 4; port++)
        {
            var instanceId = DeviceInstanceId.FromInterfacePath(
                $"{FtdiQuadPortInterfacePathPrefix}{port}#0000#{ComPortInterfaceGuid}")!;

            Assert.Null(DeviceInstanceId.ReadSerialNumber(instanceId));
        }
    }

    [Fact]
    public void ReadLocation_FtdibusWithoutASerial_DistinguishesEveryPortOfTheChip()
    {
        var locations = new List<string>();

        for (var port = 1; port <= 4; port++)
        {
            var instanceId = DeviceInstanceId.FromInterfacePath(
                $"{FtdiQuadPortInterfacePathPrefix}{port}#0000#{ComPortInterfaceGuid}")!;

            var location = DeviceInstanceId.ReadLocation(instanceId);
            Assert.Equal($"6&2F625D12&0&1&{port}", location);
            locations.Add(location!);
        }

        // 位置的意义全在"四个口各不相同"上：如果它们相同，这个字段就没有存在的理由。
        Assert.Equal(4, new HashSet<string>(locations).Count);
    }

    [Theory]
    // 普通 USB 形态：驱动写了序列号。
    [InlineData(@"USB\VID_0403&PID_6001\A50285BI", "A50285BI")]
    // 末段是位置路径，不是序列号 —— 返回位置而不是把它当 SN 显示出来。
    [InlineData(@"USB\VID_1A86&PID_7523\5&1b4e8a2c&0&1", null)]
    // 既没有序列号、末段又是设备对象序号：退到前一段拿到的是硬件 id，它不是序列号也不是位置。
    [InlineData(@"USB\VID_0403&PID_6001\0000", null)]
    // 一两字符的尾巴是噪声，不是序列号。
    [InlineData(@"USB\VID_0403&PID_6001\1", null)]
    // 末段就是 VID 前言本身。
    [InlineData(@"USB\VID_0403&PID_6001\VID_0403", null)]
    // 缺少身份段的截断实例 id，以及纯前缀输入。
    [InlineData(@"USB\VID_0403&PID_6001", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadSerialNumber_GenericUsbForms(string? instanceId, string? expected)
    {
        Assert.Equal(expected, DeviceInstanceId.ReadSerialNumber(instanceId));
    }

    [Theory]
    // 普通 USB 形态的位置。
    [InlineData(@"USB\VID_1A86&PID_7523\5&1b4e8a2c&0&1", "5&1b4e8a2c&0&1")]
    // 硬件 id 段含 '&'，但它不是位置 —— 一个没序列号的设备退到这一段时，绝不能把它显示成位置。
    [InlineData(@"USB\VID_0403&PID_6001\0000", null)]
    [InlineData(@"USB\VID_0403&PID_6001\A50285BI", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ReadLocation_GenericUsbForms(string? instanceId, string? expected)
    {
        Assert.Equal(expected, DeviceInstanceId.ReadLocation(instanceId));
    }

    [Fact]
    public void ReadSerialNumber_OnlySplitsOnTheVidPidForm()
    {
        // '+' 只对 FTDIBUS 那类 "VID_xxxx+PID_xxxx+<身份>" 的段有含义。普通形态里序列号自带 '+' 时不能被切掉。
        Assert.Equal("AB+CD123", DeviceInstanceId.ReadSerialNumber(@"USB\VID_0403&PID_6001\AB+CD123"));
    }

    [Fact]
    public void ReadSerialNumber_OverlongValue_IsTruncated()
    {
        var serial = DeviceInstanceId.ReadSerialNumber($@"USB\VID_0403&PID_6001\{new string('X', 45)}");

        Assert.Equal(new string('X', 40), serial);
    }

    // ---- 注册表值的间接字符串 ----------------------------------------------------------------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("FTDI", "FTDI")]
    [InlineData("  FTDI  ", "FTDI")]
    // 普通文本里的分号不是分隔符。
    [InlineData("Foo; Inc", "Foo; Inc")]
    [InlineData("@oem12.inf,%ftdi%;FTDI USB Serial Port", "FTDI USB Serial Port")]
    [InlineData("@oem12.inf,%ftdi%;", null)]
    // 裸占位符没有回退文本，显示出来只会被当成乱码。
    [InlineData("%ftdi%", null)]
    public void HumanizeIndirectString_ReturnsTheFallbackText(string? raw, string? expected)
    {
        Assert.Equal(expected, DeviceInstanceId.HumanizeIndirectString(raw));
    }
}
