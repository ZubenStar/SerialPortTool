namespace SerialPortTool.Models;

/// <summary>
/// 一个串口背后的设备标识信息（厂商 / VID / PID / 序列号）。
/// </summary>
/// <remarks>
/// Read from the Plug and Play device node, never by opening the port — see
/// <c>Services.ISerialPortDeviceEnumerator</c> for why that distinction is the whole design. Every field is
/// optional: a PCI serial card has no USB identifiers, and a driver that declines to publish one is not an
/// error.
/// </remarks>
public sealed class PortDeviceInfo
{
    /// <summary>串口名（<c>COM3</c>）。它是与端口列表做关联合并的键。</summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>厂商名，例如 <c>FTDI</c>。</summary>
    public string? Manufacturer { get; set; }

    /// <summary>设备描述，例如 <c>USB-SERIAL CH340</c>。</summary>
    public string? Description { get; set; }

    /// <summary>USB Vendor ID（十进制/十六进制字符串，保留设备给的写法）。</summary>
    public string? VendorId { get; set; }

    /// <summary>USB Product ID。</summary>
    public string? ProductId { get; set; }

    /// <summary>设备序列号（驱动真的写了才有；只有 USB 位置的设备这里是空的）。</summary>
    public string? SerialNumber { get; set; }

    /// <summary>设备的 USB 位置，例如 <c>6&amp;2F625D12&amp;0&amp;1&amp;2</c>。</summary>
    /// <remarks>
    /// 与 <see cref="SerialNumber"/> 语义不同，不能互换：序列号是设备的身份（换台机器、换个 USB 口都还是它），
    /// 位置只说明它插在哪个口上——同一块板子换一个 USB 口，位置就变了。多口板子与廉价芯片常常不烧序列号，
    /// Windows 于是用位置生成设备实例 id，这时位置是唯一能区分同一颗芯片几个口的东西，所以它单列一个字段，
    /// 而不是塞进序列号里，让"换个 USB 口"看起来像"换了台设备"。
    /// </remarks>
    public string? Location { get; set; }

    /// <summary>
    /// True when anything beyond the port name could be resolved, so the UI can decide whether a second
    /// line in the row is worth the space.
    /// </summary>
    /// <remarks>
    /// 这一组刻意与端口行第二行真正能显示的内容对齐（厂商 / 描述 / VID / PID）。<see cref="SerialNumber"/> 与
    /// <see cref="Location"/> 只出现在悬停详情里，只有它们时行内并没有第二行可画。
    /// 注：当前没有读取方——驱动列表的是 <c>AvailablePortItem.HasDeviceDetails</c>（由 DeviceLine 推导）。保留它
    /// 是让"这个设备到底有没有可用信息"这个概念留在模型上；改动时两边的语义要保持一致。
    /// </remarks>
    public bool HasDetails =>
        !string.IsNullOrWhiteSpace(Manufacturer) ||
        !string.IsNullOrWhiteSpace(Description) ||
        !string.IsNullOrWhiteSpace(VendorId) ||
        !string.IsNullOrWhiteSpace(ProductId);
}
