using System;
using System.Threading;
using System.Threading.Tasks;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 应用更新检查服务接口。
/// 负责查询 GitHub Releases 上的最新版本、做版本号数值比较，并维护「失败退避」与「跳过此版本」策略。
/// </summary>
/// <remarks>
/// 节流语义是「失败才退避，成功不节流」：静默检查每次启动都联网，只有失败后的 1 小时内不再重试。
/// 因此不存在「上次检查成功 → 一段时间内不查」的抑制，新发布的版本不会因为缓存而被漏掉。
/// </remarks>
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
    /// <c>true</c>：用户主动触发，<b>始终立即联网</b>并返回可读结果（含失败原因），不读写任何退避状态；
    /// <c>false</c>：启动 / 运行期静默检查，失败静默返回 <see cref="Core.Enums.UpdateCheckStatus.Failed"/>，
    /// 仅当上一次静默检查失败且距今不足 1 小时（失败退避窗口内），或命中「跳过此版本」时，
    /// 返回 <see cref="Core.Enums.UpdateCheckStatus.Skipped"/>。
    /// </param>
    /// <param name="cancellationToken">取消标记</param>
    /// <remarks>
    /// 必须保持异步：失败的静默检查需要读写退避时间戳，而设置服务内部使用信号量 + 文件 I/O，
    /// 在 UI 线程上同步等待会阻塞其续体而造成死锁。
    /// </remarks>
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录「跳过此版本」，后续静默检查不再提示该版本。
    /// </summary>
    /// <param name="version">要跳过的版本号，例如 <c>1.8.14</c></param>
    Task SkipVersionAsync(string version);
}
