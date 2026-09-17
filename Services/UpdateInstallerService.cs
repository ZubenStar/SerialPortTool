using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SerialPortTool.Services;

/// <summary>
/// 更新安装器服务实现。
/// </summary>
/// <remarks>
/// 设计约束（不得回退）：
/// <list type="bullet">
/// <item>未通过校验（长度 / 非空 / <c>MZ</c> 头）的文件一律不得 <see cref="Process.Start(ProcessStartInfo)"/> 启动，
/// 否则会把 403 / HTML 错误页当作安装包执行。</item>
/// <item>便携版（非安装版）一律不得执行静默替换，只允许提示并打开下载页。</item>
/// <item>启动安装器前必须由调用方先 Flush 设置，退出走既有 <c>App.OnWindowClosed</c>（5s 硬超时）路径。</item>
/// <item>重启主程序由安装包负责（<c>installer/SerialPortTool.iss</c> 的 <c>[Code]</c> 段），
/// 不得改用 <c>/RESTARTAPPLICATIONS</c>：它只能重启被 Restart Manager 关闭的进程，
/// 而主程序在安装器启动后已自行退出，结果是更新装完却没有程序被拉起。</item>
/// </list>
/// </remarks>
public sealed class UpdateInstallerService : IUpdateInstallerService
{
    /// <summary>
    /// 静默安装参数。<c>/CLOSEAPPLICATIONS</c> 让安装器在需要时（例如用户手动双击安装包）
    /// 由 Restart Manager 关闭仍在运行的主程序。
    /// </summary>
    /// <remarks>
    /// 刻意**不传** <c>/RESTARTAPPLICATIONS</c>：该开关（对应 <c>[Setup] RestartApplications</c>）
    /// 只会重启「被 Restart Manager 关闭」的进程，而自动更新路径下主程序在启动安装器后已自行退出，
    /// 于是更新装完却没有任何进程被拉起。重启改由 <c>installer/SerialPortTool.iss</c> 的
    /// <c>[Code]</c> 段在静默模式下显式 <c>Exec</c>，<c>RestartApplications</c> 保持 <c>no</c>
    /// 以避免两条路径同时生效而拉起两个实例。
    /// </remarks>
    private const string SilentInstallArguments =
        "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL";

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ILogger<UpdateInstallerService> _logger;
    private readonly string _updateDirectory;

    private string? _downloadedInstallerPath;
    private bool _disposed;

    public UpdateInstallerService(ILogger<UpdateInstallerService> logger)
    {
        _logger = logger;
        _updateDirectory = Path.Combine(Path.GetTempPath(), "SerialPortTool", "Update");

        // 启动即清理历史残留，避免临时目录无限增长。
        CleanupStaleDownloads();
    }

    /// <inheritdoc />
    public async Task<bool> DownloadInstallerAsync(
        string downloadUrl,
        long expectedSizeBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return false;
        }

        // 闸门：只有安装版才允许自动替换，便携版绝不静默覆盖用户目录。
        if (!InstalledBuildInfo.IsInstalled())
        {
            _logger.LogInformation("Skip installer download: current build is portable (not installed)");
            return false;
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            _logger.LogWarning("Skip installer download: empty download url");
            return false;
        }

        string targetPath;
        try
        {
            Directory.CreateDirectory(_updateDirectory);
            targetPath = Path.Combine(_updateDirectory, BuildInstallerFileName(downloadUrl));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare update directory {Directory}", _updateDirectory);
            return false;
        }

        try
        {
            progress?.Report(0d);

            using var response = await Http
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Installer download failed with HTTP {StatusCode}", (int)response.StatusCode);
                return false;
            }

            var totalBytes = expectedSizeBytes > 0
                ? expectedSizeBytes
                : response.Content.Headers.ContentLength ?? 0;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastReportedPercent = -1;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (totalBytes > 0 && progress != null)
                    {
                        var percent = (int)(received * 100 / totalBytes);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress.Report(Math.Min(received / (double)totalBytes, 1d));
                        }
                    }
                }
            }

            if (!VerifyDownloadedInstaller(targetPath, expectedSizeBytes))
            {
                TryDelete(targetPath);
                return false;
            }

            _downloadedInstallerPath = targetPath;
            progress?.Report(1d);
            _logger.LogInformation("Installer downloaded and verified: {Path}", targetPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(targetPath);
            _logger.LogInformation("Installer download cancelled by user");
            return false;
        }
        catch (HttpRequestException ex)
        {
            TryDelete(targetPath);
            _logger.LogWarning(ex, "Installer download failed: network error");
            return false;
        }
        catch (Exception ex)
        {
            TryDelete(targetPath);
            _logger.LogError(ex, "Installer download failed");
            return false;
        }
    }

    /// <inheritdoc />
    public void LaunchInstaller()
    {
        var path = _downloadedInstallerPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("安装程序尚未下载完成，无法启动更新");
        }

        // 再次校验，防止文件在下载后被替换或截断。
        if (!VerifyDownloadedInstaller(path, 0))
        {
            throw new InvalidOperationException("安装程序校验失败，已中止更新");
        }

        _logger.LogInformation("Launching silent installer: {Path}", path);

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = SilentInstallArguments,
            UseShellExecute = true
        });
    }

    /// <inheritdoc />
    public void CleanupStaleDownloads()
    {
        try
        {
            if (!Directory.Exists(_updateDirectory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_updateDirectory))
            {
                TryDelete(file);
            }
        }
        catch (Exception ex)
        {
            // 清理失败不影响主流程（可能有另一个实例正在下载）。
            _logger.LogDebug(ex, "Failed to clean up stale update downloads in {Directory}", _updateDirectory);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // 大体积安装包下载由 CancellationToken 控制
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("SerialPortTool-Updater");
        return client;
    }

    /// <summary>
    /// 校验下载得到的安装程序：非空、长度匹配（若已知）、PE 头为 <c>MZ</c>。
    /// </summary>
    private bool VerifyDownloadedInstaller(string path, long expectedSizeBytes)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                _logger.LogWarning("Downloaded installer is missing or empty: {Path}", path);
                return false;
            }

            if (expectedSizeBytes > 0 && fileInfo.Length != expectedSizeBytes)
            {
                _logger.LogWarning(
                    "Downloaded installer size mismatch: expected {Expected} bytes, got {Actual}",
                    expectedSizeBytes, fileInfo.Length);
                return false;
            }

            Span<byte> header = stackalloc byte[2];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Read(header) != header.Length)
                {
                    _logger.LogWarning("Downloaded installer is too small to be a PE file: {Path}", path);
                    return false;
                }
            }

            if (header[0] != (byte)'M' || header[1] != (byte)'Z')
            {
                _logger.LogWarning("Downloaded installer is not a valid executable (missing MZ header): {Path}", path);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify downloaded installer {Path}", path);
            return false;
        }
    }

    private static string BuildInstallerFileName(string downloadUrl)
    {
        try
        {
            var name = Path.GetFileName(new Uri(downloadUrl).AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        catch (UriFormatException)
        {
            // 落到默认文件名
        }

        return "SerialPortTool-Setup.exe";
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete file {Path}", path);
        }
    }
}

/// <summary>
/// 安装版判定（供 <see cref="UpdateService"/> 与 <see cref="UpdateInstallerService"/> 共用）。
/// </summary>
/// <remarks>
/// <see cref="UninstallRegistryKey"/> 中的 AppId 必须与 <c>installer/SerialPortTool.iss</c> 的
/// <c>AppId</c> 完全一致；一旦发布不可更改，否则老版本无法被覆盖升级。
/// </remarks>
internal static class InstalledBuildInfo
{
    /// <summary>Inno Setup 的 <c>AppId</c>（与 installer/SerialPortTool.iss 保持一致）。</summary>
    internal const string AppId = "{7B4E2C19-3A6D-4F82-9E51-0C8A5D3F1B74}";

    /// <summary>Inno Setup 在用户级安装时写入的卸载信息注册表项。</summary>
    internal const string UninstallRegistryKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";

    /// <summary>安装目录名（位于 <c>%LOCALAPPDATA%\Programs</c> 下）。</summary>
    internal const string InstallationFolderName = "SerialPortTool";

    /// <summary>
    /// 当前进程是否由安装包部署。
    /// </summary>
    internal static bool IsInstalled()
    {
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            var expectedDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                InstallationFolderName);

            if (!IsUnder(baseDirectory, expectedDirectory))
            {
                return false;
            }

            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey);
            return key != null;
        }
        catch
        {
            // 任何异常都按「非安装版」处理，宁可只提示也不误替换用户目录。
            return false;
        }
    }

    private static bool IsUnder(string path, string parentDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(parentDirectory))
        {
            return false;
        }

        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory))
                               + Path.DirectorySeparatorChar;
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
                             + Path.DirectorySeparatorChar;

        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }
}
