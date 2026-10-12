using System;
using System.Text.RegularExpressions;

namespace SerialPortTool.Helpers;

/// <summary>
/// 设备实例 id 的纯解析：接口路径还原、VID / PID 提取、序列号判定、USB 位置提取、INF 间接字符串人性化。
/// </summary>
/// <remarks>
/// <para>
/// 这些逻辑原先住在 <c>Services.SerialPortDeviceEnumerator</c> 里。把它们剥出来的理由是可测性：它们是纯字符串
/// 启发式，而那个类整体依赖 WinRT 设备枚举与注册表，进不了逻辑层测试工程（它刻意不引用 app 工程，只链接
/// UI-free 的源文件）。剥出来之后，真机上真实的实例路径可以原样当测试数据——对一个"看着像序列号其实只是数字"
/// 的启发式来说，这是唯一靠得住的验证方式。
/// </para>
/// <para>
/// 契约：全部是纯字符串操作，<b>任何输入都不抛异常</b>，取不到就返回 null。调用方（端口扫描）把这些值直接
/// 放进端口列表，所以"这台机器的驱动没有写序列号"必须表现为一个空字段，而不是一次失败的扫描。
/// </para>
/// </remarks>
public static class DeviceInstanceId
{
    /// <summary>USB 标识符的定长十六进制写法（<c>VID_0403</c> / <c>PID_6001</c>）。</summary>
    private static readonly Regex VidPattern =
        new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PidPattern =
        new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>设备对象序号的长度上限：形如 <c>0000</c>。</summary>
    private const int MaxUnitIndexLength = 4;

    /// <summary>身份值的最短长度：一两字符的尾巴是噪声，不是序列号。</summary>
    private const int MinValueLength = 3;

    /// <summary>序列号的长度上限，超出的部分截断。</summary>
    private const int MaxSerialLength = 40;

    /// <summary>
    /// 把设备接口路径（<c>\\?\USB#VID_1A86&amp;PID_7523#5&amp;1b4e8a2c&amp;0&amp;1#{guid}</c>）还原成设备实例 id
    /// （<c>USB\VID_1A86&amp;PID_7523\5&amp;1b4e8a2c&amp;0&amp;1</c>）；无法还原时返回 null。
    /// </summary>
    public static string? FromInterfacePath(string? deviceInterfaceId)
    {
        if (string.IsNullOrEmpty(deviceInterfaceId))
        {
            return null;
        }

        var trimmed = deviceInterfaceId;

        const string prefix = @"\\?\";
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[prefix.Length..];
        }

        // Drop the trailing interface-class GUID segment: everything after the last '#{'.
        var guidStart = trimmed.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidStart > 0)
        {
            trimmed = trimmed[..guidStart];
        }

        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Replace('#', '\\');
    }

    /// <summary>USB Vendor ID（大写十六进制）；实例 id 里没有 <c>VID_xxxx</c> 时返回 null。</summary>
    public static string? ReadVendorId(string? instanceId) => FirstMatch(VidPattern, instanceId);

    /// <summary>USB Product ID（大写十六进制）；实例 id 里没有 <c>PID_xxxx</c> 时返回 null。</summary>
    public static string? ReadProductId(string? instanceId) => FirstMatch(PidPattern, instanceId);

    /// <summary>
    /// 序列号：设备实例 id 里那段"身份"，但只在它真的是序列号时返回；只有位置、没有烧录序列号时返回 null。
    /// </summary>
    /// <remarks>
    /// There is no dedicated "serial number" value every driver publishes, so this uses what Windows itself uses
    /// to tell two adapters apart — see <see cref="IdentitySegment"/> for where that lives in the instance id.
    /// Best-effort by nature: a tail that is not an identity at all is dropped rather than shown, because `SN：0000`
    /// in every row is worse than nothing.
    /// </remarks>
    public static string? ReadSerialNumber(string? instanceId)
    {
        var identity = IdentitySegment(instanceId);

        // 带 '&' 的是 USB 位置路径（hub&port...），不是序列号 —— 见 IsLocation。
        if (!IsUsableIdentity(identity) || identity!.Contains('&'))
        {
            return null;
        }

        return identity.Length > MaxSerialLength ? identity[..MaxSerialLength] : identity;
    }

    /// <summary>
    /// USB 位置：设备插在哪个口上（<c>6&amp;2F625D12&amp;0&amp;1&amp;2</c>）；实例 id 里没有位置信息时返回 null。
    /// </summary>
    /// <remarks>
    /// 与序列号是同一段文本的两个互斥解释：带 <c>&amp;</c> 的必然是位置，不带的是驱动写的序列号。廉价芯片与多口
    /// 板子常常不烧序列号，Windows 于是用"插在哪个口上"生成设备实例 id —— 那种情况下位置是唯一能区分同一颗芯片
    /// 几个口的东西，所以它必须单独可见，而不是被当成序列号显示（那会让一堆口看起来像同一台设备）或者被丢掉。
    /// 位置不截断：末尾那一段正是端口序号，截掉的恰好是唯一有用的部分。
    /// </remarks>
    public static string? ReadLocation(string? instanceId)
    {
        var identity = IdentitySegment(instanceId);
        return IsLocation(identity) ? identity : null;
    }

    /// <summary>
    /// <c>DeviceDesc</c> 与 <c>Mfg</c> 常常是 <c>@oem12.inf,%key%;文本</c> 这样的间接引用，取分号后面的回退文本。
    /// </summary>
    /// <remarks>
    /// 占位符（<c>%key%</c>）只有 INF 解析器能展开，注册表层做不到。所以取不到回退文本时（没有分号、分号在
    /// 末尾、或整个值就是一个裸占位符）返回 null 而不是原样透传：把 <c>@oem12.inf,%ftdi%;</c> 显示进端口行
    /// 只会被用户当成乱码。普通文本（<c>FTDI</c>、<c>wch.cn</c>）不以 <c>@</c> 或 <c>%</c> 开头，原样返回——
    /// 它们中间的分号（"Foo; Inc"）不是分隔符，不能截。
    /// </remarks>
    public static string? HumanizeIndirectString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw!.Trim();

        if (!value.StartsWith('@') && !value.StartsWith('%'))
        {
            return value;
        }

        var semicolon = value.IndexOf(';');
        if (semicolon < 0)
        {
            // 间接引用却没有回退文本：占位符本身不是给人看的。
            return null;
        }

        var fallback = value[(semicolon + 1)..].Trim();
        return fallback.Length > 0 ? fallback : null;
    }

    /// <summary>
    /// 从设备实例 id 里取出"身份那一段"（序列号或 USB 位置），取不到返回 null。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 取值顺序，每一步都对应一种真实存在的实例 id 形态：
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// 先取最后一段。但 FTDIBUS 及其同类总线枚举器会在实例 id 末尾恒定加一段设备对象序号（FTDIBUS 下它就是
    /// 字面的 <c>0000</c>），它不含任何身份信息。原先"取最后一段"的实现因此让一块四口板子的四个口、乃至同机上
    /// 任何一颗 FTDI 芯片，全部显示同一个序列号 <c>0000</c>——比没有更糟，因为它看起来像个真实值。
    /// </description></item>
    /// <item><description>
    /// 若末段是这种恒定序号就退到前一段，那才是驱动写身份的地方。
    /// </description></item>
    /// <item><description>
    /// 该段若为 FTDIBUS 的 <c>VID_xxxx+PID_xxxx+&lt;身份&gt;</c> 形式，身份在最后一个 <c>+</c> 之后。只对含
    /// <c>VID_</c> 的段做这个切分，普通形态里恰好含 <c>+</c> 的序列号不会被误切。
    /// </description></item>
    /// </list>
    /// </remarks>
    private static string? IdentitySegment(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
        {
            return null;
        }

        var segments = instanceId.Split('\\');
        var candidate = segments[^1];

        if (IsUnitIndex(candidate) && segments.Length > 1)
        {
            candidate = segments[^2];
        }

        if (VidPattern.IsMatch(candidate))
        {
            var plus = candidate.LastIndexOf('+');
            if (plus >= 0)
            {
                candidate = candidate[(plus + 1)..];
            }
        }

        return candidate;
    }

    /// <summary>形如 <c>0000</c> 的设备对象序号：全零且短，不是身份。</summary>
    private static bool IsUnitIndex(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > MaxUnitIndexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character != '0')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>一个可显示的身份值：既不是硬件 id 段（<c>VID_xxxx|PID_xxxx</c>），也不是空串或噪声。</summary>
    private static bool IsUsableIdentity(string? value)
        => value is not null
           && value.Length >= MinValueLength
           && !VidPattern.IsMatch(value)
           && !PidPattern.IsMatch(value);

    /// <summary>
    /// 位置路径判定：USB 位置一定是 <c>hub&amp;port...</c> 这种带 <c>&amp;</c> 的串，序列号不带。
    /// </summary>
    /// <remarks>
    /// 硬件 id 段（<c>VID_0403&amp;PID_6001</c>）本身也含 <c>&amp;</c>，必须排除：一个既没序列号、末段又是
    /// <c>0000</c> 的设备，退到前一段拿到的正是硬件 id，不排除的话它会被当成"位置"显示出来。
    /// </remarks>
    private static bool IsLocation(string? value)
        => value is not null
           && value.Length >= MinValueLength
           && value.Contains('&')
           && !VidPattern.IsMatch(value);

    private static string? FirstMatch(Regex pattern, string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var match = pattern.Match(input);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
