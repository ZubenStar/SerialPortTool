using System;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 设置服务接口
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 已存在的 <c>settings.json</c> 无法解析时触发（每个进程最多一次）。
    /// </summary>
    /// <remarks>
    /// 触发即意味着该服务已进入<b>只读保护</b>状态：内存里没有真实内容，继续写盘会用空字典覆盖用户
    /// 的端口配置 / 外观 / 颜色 / 搜索历史。订阅方应给用户一个明确、可操作的提示，而不是静默降级。
    /// </remarks>
    event EventHandler? SettingsLoadFailed;

    /// <summary>
    /// 是否处于「设置文件读取失败，已暂停写盘」的保护状态。
    /// </summary>
    /// <remarks>
    /// 事件可能在订阅者（ViewModel / Window）存在之前就已经触发过，订阅方必须在初始化时也读一次本属性。
    /// </remarks>
    bool HasLoadFailure { get; }

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

    /// <summary>
    /// 立即把内存缓存中尚未落盘的设置写入磁盘。
    /// </summary>
    /// <remarks>
    /// 普通的 SaveSettingAsync 会把变更存入内存缓存并启动一个 500ms 防抖定时器，
    /// 这是为了避免连续按键（发送框、搜索历史等）导致整文件 JSON 反复重写。
    /// FlushAsync 在关窗 / DI Dispose 时调用以确保最后一次变更不会丢失。
    /// </remarks>
    Task FlushAsync();
}