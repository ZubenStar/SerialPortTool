using System;
using System.Threading;
using System.Threading.Tasks;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 应用更新检查服务接口。
/// 负责查询 GitHub Releases 上的最新版本、做版本号数值比较，并维护「上次检查时间」缓存与「跳过此版本」策略。
/// </summary>
public interface IUpdateService : IDisposable
{
    /// <summary>
    /// 当前进程是否由安装包部署（决定能否执行静默替换）。
    /// 便携版（直接解压 ZIP 运行）返回 <c>false</c>，此时只能提示并打开下载页。
    /// </summary>
    bool IsInstalledBuild { get; }

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <param name="manual">
    /// <c>true</c>：用户主动触发，始终返回可读结果（含失败原因），忽略 24 小时缓存与跳过策略；
    /// <c>false</c>：启动时静默检查，失败静默返回 <see cref="Core.Enums.UpdateCheckStatus.Failed"/>，
    /// 距上次检查不足缓存时长或命中已跳过版本时返回 <see cref="Core.Enums.UpdateCheckStatus.Skipped"/>。
    /// </param>
    /// <param name="cancellationToken">取消标记</param>
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录「跳过此版本」，后续静默检查不再提示该版本。
    /// </summary>
    /// <param name="version">要跳过的版本号，例如 <c>1.8.14</c></param>
    Task SkipVersionAsync(string version);

    /// <summary>
    /// 判断距上次成功检查是否在指定小时数以内（用于静默检查节流）。
    /// </summary>
    /// <param name="withinHours">缓存时长（小时）</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <remarks>
    /// 必须保持异步：设置服务内部使用信号量 + 文件 I/O，在 UI 线程上同步等待会阻塞其续体而造成死锁。
    /// </remarks>
    Task<bool> IsRecentCheckAsync(int withinHours, CancellationToken cancellationToken = default);
}
