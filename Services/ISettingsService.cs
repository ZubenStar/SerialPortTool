using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 设置服务接口
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 保存整数设置
    /// </summary>
    Task SaveSettingAsync(string key, int value);

    /// <summary>
    /// 加载整数设置
    /// </summary>
    Task<int> LoadSettingAsync(string key, int defaultValue);

    /// <summary>
    /// 保存字符串设置
    /// </summary>
    Task SaveSettingAsync(string key, string value);

    /// <summary>
    /// 加载字符串设置
    /// </summary>
    Task<string> LoadSettingAsync(string key, string defaultValue);

    /// <summary>
    /// 删除设置
    /// </summary>
    Task DeleteSettingAsync(string key);

    /// <summary>
    /// 清除所有设置
    /// </summary>
    Task ClearAsync();
}