using SerialPortTool.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 读取串口背后的设备标识（厂商 / VID / PID / 序列号），用来给端口列表补充信息。
/// </summary>
public interface ISerialPortDeviceEnumerator
{
    /// <summary>
    /// 枚举当前存在的串口设备。
    /// </summary>
    /// <returns>
    /// 以串口名（<c>COM3</c>，大小写不敏感）为键的设备信息字典。查不到的部分留空；整体失败时返回空字典。
    /// </returns>
    /// <remarks>
    /// Never throws. This is the <b>only</b> promise the caller gets, and it is the important one: the
    /// information this returns decorates a row in a list. A machine whose drivers do not publish it, a
    /// policy that blocks device enumeration, or a future Windows version that changes the shape of the
    /// device node must all cost the user a missing second line — never a failed port scan.
    /// </remarks>
    Task<IReadOnlyDictionary<string, PortDeviceInfo>> EnumerateAsync();
}
