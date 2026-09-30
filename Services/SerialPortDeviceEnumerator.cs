using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;

namespace SerialPortTool.Services;

/// <summary>
/// WinRT 设备枚举的接口实现：<b>只查询，不打开</b>。
/// </summary>
/// <remarks>
/// <para>
/// The one rule this class must not break is in the interface comment, and it deserves the reason written
/// down: <c>SerialDevice.FromIdAsync</c> would hand back the port name directly, and every article about
/// this problem suggests it. It also <b>opens the port</b>. Doing that during a scan would put the process
/// in the position of having opened every serial device on the machine and then failed to hand them to
/// <c>System.IO.Ports</c>, which raises <c>UnauthorizedAccessException</c> for the very ports it just
/// listed. So the port name comes from the Plug and Play device node instead, and nothing ever takes the
/// handle.
/// </para>
/// <para>
/// How the mapping works. <c>SerialDevice.GetDeviceSelector()</c> narrows the device query to serial
/// devices only, and each result's <c>Id</c> is a device <em>interface</em> path:
/// <c>\\?\USB#VID_1A86&amp;PID_7523#5&amp;1b4e8a2c&amp;0&amp;1#{...guid}</c>. The instance id is that path with the
/// leading <c>\\?\</c> and the trailing <c>#{guid}</c> removed and every remaining <c>#</c> restored to a
/// <c>\</c>. Windows writes the COM name the same place it reads it from afterwards: that device's own
/// <c>Device Parameters\PortName</c> value. Everything else shown in the row (manufacturer, description,
/// the identifiers themselves) is read from the same node.
/// </para>
/// <para>
/// Failure is a normal outcome here, not an exceptional one: onboard serial ports have no USB identifiers,
/// virtual COM drivers often publish no manufacturer, and <c>DeviceInformation</c> can return a node whose
/// registry key this process may not read. Each of those yields a partially filled
/// <see cref="PortDeviceInfo"/> or no entry at all, and the caller keeps going.
/// </para>
/// </remarks>
public sealed class SerialPortDeviceEnumerator : ISerialPortDeviceEnumerator
{
    /// <summary>
    /// Hard ceiling on the device query.
    /// </summary>
    /// <remarks>
    /// Device enumeration is not under this process's control — it can block for seconds on a machine with
    /// a wedged driver — and it runs inside <c>ScanPortsAsync</c>, which a user triggers to make the port
    /// list appear. Unbounded, a hung query would hang the scan. Bounded, it costs one startup the extra
    /// line in the row and nothing else.
    /// </remarks>
    public const int QueryTimeoutMs = 3000;

    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    private static readonly Regex VidPattern =
        new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PidPattern =
        new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<SerialPortDeviceEnumerator> _logger;

    public SerialPortDeviceEnumerator(ILogger<SerialPortDeviceEnumerator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, PortDeviceInfo>> EnumerateAsync()
    {
        var byPort = new Dictionary<string, PortDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        DeviceInformationCollection? devices;
        try
        {
            using var timeout = new CancellationTokenSource(QueryTimeoutMs);
            devices = await DeviceInformation
                .FindAllAsync(SerialDevice.GetDeviceSelector())
                .AsTask(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning("Serial device enumeration timed out after {Ms} ms; showing port names only",
                QueryTimeoutMs);
            return byPort;
        }
        catch (Exception ex)
        {
            // Unpackaged apps, locked-down machines and machines with no serial device nodes at all all
            // land here, and none of them is a defect in the port list.
            _logger.LogWarning(ex, "Serial device enumeration is unavailable; showing port names only");
            return byPort;
        }

        foreach (var device in devices)
        {
            try
            {
                var info = BuildInfo(device.Id);
                if (info is not null)
                {
                    byPort[info.PortName] = info;
                }
            }
            catch (Exception ex)
            {
                // One malformed node must not cost the other rows their detail line.
                _logger.LogDebug(ex, "Could not read device metadata from '{DeviceId}'", device.Id);
            }
        }

        return byPort;
    }

    /// <summary>
    /// 把一个设备接口路径还原成完整的 <see cref="PortDeviceInfo"/>；无法确定串口名时返回 null。
    /// </summary>
    private PortDeviceInfo? BuildInfo(string deviceInterfaceId)
    {
        var instanceId = ParseInstanceId(deviceInterfaceId);
        if (instanceId is null)
        {
            return null;
        }

        // Walk the instance id itself and then its parents: a composite USB device assigns the COM name to
        // the child function, whose own node frequently has no Device Parameters of its own.
        var portName = ResolvePortName(instanceId);
        for (var candidate = instanceId; candidate is not null; candidate = Parent(candidate))
        {
            portName ??= TryReadValue(candidate, string.Empty, "PortName");
            if (portName is not null)
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        return new PortDeviceInfo
        {
            PortName = portName!,
            Manufacturer = TryReadValue(instanceId, string.Empty, "Mfg"),
            Description = HumanizeDeviceDesc(TryReadValue(instanceId, string.Empty, "DeviceDesc")),
            VendorId = FirstMatch(VidPattern, instanceId),
            ProductId = FirstMatch(PidPattern, instanceId),
            SerialNumber = ReadSerialNumber(instanceId),
        };
    }

    /// <summary>先从 <c>Device Parameters</c> 找，再退到设备键本身。</summary>
    private static string? ResolvePortName(string instanceId)
        => TryReadValue(instanceId, "Device Parameters", "PortName")
           ?? TryReadValue(instanceId, string.Empty, "PortName");

    /// <summary>
    /// 把设备接口路径（<c>\\?\USB#VID_...#{guid}</c>）还原成设备实例 id（<c>USB\VID_...</c>）。
    /// </summary>
    private static string? ParseInstanceId(string deviceInterfaceId)
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

    /// <summary>实例 id 去掉最后一段。返回 null 表示已经到根。</summary>
    private static string? Parent(string instanceId)
    {
        var separator = instanceId.LastIndexOf('\\');
        return separator <= 0 ? null : instanceId[..separator];
    }

    private static string? TryReadValue(string instanceId, string subKeyName, string valueName)
    {
        var path = subKeyName.Length == 0 ? instanceId : $@"{instanceId}\{subKeyName}";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{path}");
            return key?.GetValue(valueName) as string;
        }
        catch (Exception)
        {
            // Unreadable node: no COM name from this one. Not logged — it is common enough (permission,
            // removed device) that per-node logging would bury everything else.
            return null;
        }
    }

    /// <summary>
    /// 序列号：设备实例 id 的最后一段，只要它不是 ID 前言或位置路径。
    /// </summary>
    /// <remarks>
    /// There is no dedicated "serial number" value every driver publishes, so this uses what Windows itself
    /// uses to tell two adapters apart — everything after the last separator in the instance id. It is
    /// best-effort by nature, which is why a short or clearly-not-a-serial tail is dropped rather than
    /// shown: `SN：1` in every row would be worse than nothing.
    /// </remarks>
    private static string? ReadSerialNumber(string instanceId)
    {
        var separator = instanceId.LastIndexOf('\\');
        if (separator < 0 || separator == instanceId.Length - 1)
        {
            return null;
        }

        var tail = instanceId[(separator + 1)..];

        // A bare VID/PID preamble or a location path is not a serial number; a tail that looks like one is
        // whatever the driver chose to distinguish two otherwise identical adapters, which is exactly what
        // the user needs in the row.
        if (VidPattern.IsMatch(tail) || PidPattern.IsMatch(tail) || tail.Length < 3)
        {
            return null;
        }

        return tail.Length > 40 ? tail[..40] : tail;
    }

    /// <summary>
    /// <c>DeviceDesc</c> 常常是 <c>@%SystemRoot%\...;文本</c> 这样的间接引用，取分号后面的部分。
    /// </summary>
    private static string? HumanizeDeviceDesc(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var semicolon = raw!.IndexOf(';');
        return semicolon >= 0 && semicolon < raw.Length - 1 ? raw[(semicolon + 1)..] : raw;
    }

    private static string? FirstMatch(Regex pattern, string input)
    {
        var match = pattern.Match(input);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
