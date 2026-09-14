using System;
using System.Threading;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// 更新安装器服务接口。
/// 负责下载安装程序、校验完整性，并把下载好的安装程序作为「独立更新器进程」静默启动
/// （由它完成「关闭主程序 → 替换文件 → 自动重启」）。
/// </summary>
public interface IUpdateInstallerService : IDisposable
{
    /// <summary>
    /// 流式下载安装程序到临时目录并校验完整性。
    /// </summary>
    /// <param name="downloadUrl">安装程序下载地址（Release 资产）</param>
    /// <param name="expectedSizeBytes">期望字节数（来自 Release 资产，0 表示跳过长度校验）</param>
    /// <param name="progress">下载进度（0–1），已在调用方约定回 UI 线程</param>
    /// <param name="cancellationToken">取消标记</param>
    /// <returns>下载并校验成功返回 <c>true</c>；便携版、校验失败或取消返回 <c>false</c></returns>
    Task<bool> DownloadInstallerAsync(
        string downloadUrl,
        long expectedSizeBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 静默启动已下载并校验通过的安装程序。
    /// 本方法不退出应用，调用方需在随后关闭窗口（既有 <c>App.OnWindowClosed</c> 清理路径会处理退出）。
    /// </summary>
    void LaunchInstaller();

    /// <summary>
    /// 清理更新下载临时目录（<c>%TEMP%\SerialPortTool\Update</c>）中的历史残留文件。
    /// </summary>
    void CleanupStaleDownloads();
}
