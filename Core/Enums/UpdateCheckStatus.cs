namespace SerialPortTool.Core.Enums;

/// <summary>
/// 更新检查结果状态
/// </summary>
public enum UpdateCheckStatus
{
    /// <summary>
    /// 当前已是最新版本
    /// </summary>
    UpToDate,

    /// <summary>
    /// 发现新版本
    /// </summary>
    UpdateAvailable,

    /// <summary>
    /// 检查失败（网络异常 / 超时 / 限流 / 解析失败）
    /// </summary>
    Failed,

    /// <summary>
    /// 本次检查被跳过（静默检查仍在节流窗口内，或命中已跳过的版本）
    /// </summary>
    Skipped
}
