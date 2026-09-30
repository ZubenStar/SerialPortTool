using SerialPortTool.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 命名串口配置档案（预设）的持久化与校验。
/// </summary>
public interface IPortPresetService
{
    /// <summary>读取预设库。</summary>
    /// <returns>按写入顺序返回的预设列表；损坏或为空时返回空列表。</returns>
    Task<IReadOnlyList<PortPreset>> LoadAsync();

    /// <summary>保存预设库。</summary>
    Task SaveAsync(IReadOnlyList<PortPreset> presets);

    /// <summary>
    /// 校验一条预设是否可以保存。
    /// </summary>
    /// <returns>null 表示通过；否则是可直接显示给用户的原因。</returns>
    string? Validate(PortPreset preset, IReadOnlyList<PortPreset> existing, string? currentId = null);

    /// <summary>用当前侧栏参数建一条新预设。</summary>
    PortPreset Create(SerialPortProfile profile, string name);
}
