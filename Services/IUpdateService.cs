using System;
using System.Threading;
using System.Threading.Tasks;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 应用更新检查服务接口。
/// 负责查询 GitHub Releases 上的最新版本、做版本号数值比较，并维护「待处理更新缓存」「失败退避」
/// 与「跳过此版本」策略。
/// </summary>
/// <remarks>
/// 规则按优先级是：<b>已知有更新就不再联网</b>（结果缓存在本地，用户点「稍后」后重启程序仍能立刻看到提示，
/// 缓存在被跳过 / 被装上 / 超过 24 小时后失效）→ 没有可用缓存时，失败退避 1 小时、
/// 两次成功之间至少间隔 30 分钟。
/// 成功侧的短冷却是必需的——不限速地把「每次启动」直接换算成请求数，会撞上 GitHub 匿名接口
/// 60 次/小时的上限（且按公网 IP 计），反而让检查整体不可用；30 分钟只把新版本的发现推迟到最多半小时，
/// 而需要立刻确认时走手动检查即可。缓存一天的兜底保证最坏情况下也不会拿着过期结论无限期提示。
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
    /// <c>true</c>：用户主动触发，<b>始终立即联网</b>并返回可读结果（含失败原因），不读写任何节流键；
    /// <c>false</c>：启动 / 运行期静默检查，先复用本地缓存的待处理更新（不联网），否则失败静默返回
    /// <see cref="Core.Enums.UpdateCheckStatus.Failed"/>，仅当处于节流窗口内（距上次失败不足 1 小时，
    /// 或距上次成功不足 30 分钟），或命中「跳过此版本」时，返回 <see cref="Core.Enums.UpdateCheckStatus.Skipped"/>。
    /// </param>
    /// <param name="cancellationToken">取消标记</param>
    /// <remarks>
    /// 必须保持异步：静默检查需要读写节流时间戳与待处理更新缓存，而设置服务内部使用信号量 + 文件 I/O，
    /// 在 UI 线程上同步等待会阻塞其续体而造成死锁。
    /// </remarks>
    Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default);

    /// <summary>
    /// 记录「跳过此版本」，后续静默检查不再提示该版本；同时丢弃本地缓存的待处理更新，
    /// 否则缓存会把刚被跳过的版本原样再端出来。
    /// </summary>
    /// <param name="version">要跳过的版本号，例如 <c>1.8.14</c></param>
    Task SkipVersionAsync(string version);
}
