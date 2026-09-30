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

    /// <summary>设备序列号。</summary>
    public string? SerialNumber { get; set; }

    /// <summary>
    /// True when anything beyond the port name could be resolved, so the UI can decide whether a second
    /// line in the row is worth the space.
    /// </summary>
    public bool HasDetails =>
        !string.IsNullOrWhiteSpace(Manufacturer) ||
        !string.IsNullOrWhiteSpace(Description) ||
        !string.IsNullOrWhiteSpace(VendorId) ||
        !string.IsNullOrWhiteSpace(SerialNumber);
}
