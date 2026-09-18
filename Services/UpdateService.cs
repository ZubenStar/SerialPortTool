using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SerialPortTool.Core.Enums;
using SerialPortTool.Helpers;
using SerialPortTool.Models;

namespace SerialPortTool.Services;

/// <summary>
/// 更新检查服务实现。
/// </summary>
/// <remarks>
/// 设计约束（不得回退）：
/// <list type="bullet">
/// <item>版本比较必须使用 <see cref="Version"/> 数值比较，禁止字符串比较（<c>1.8.10</c> 会被判成小于 <c>1.8.9</c>）。</item>
/// <item>GitHub API 必须携带 <c>User-Agent</c>（否则 403）与 <c>Accept: application/vnd.github+json</c>。</item>
/// <item>静默检查<b>每次启动都联网</b>，不做「成功即 24 小时不查」的节流：那种节流会让刚发布的版本最长一天内
/// 不被提示（v2.1.2 之前的行为，已被实测复现）。</item>
/// <item>但<b>失败必须退避</b>（<see cref="SilentFailureBackoffHours"/> 小时，含被限流 403/429），
/// 否则断网或限流时会随每次启动变成请求风暴——这是可靠性要求而非优化。</item>
/// <item>手动检查（<c>manual: true</c>）始终立即联网，且不读写任何退避状态。</item>
/// <item>静默检查失败必须静默（Debug 日志），不得在断网时产生错误风暴。</item>
/// </list>
/// </remarks>
public sealed class UpdateService : IUpdateService
{
    private const string RepoOwner = "ZubenStar";
    private const string RepoName = "SerialPortTool";

    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

    /// <summary>
    /// 静默检查失败后的退避时长（小时）。成功检查不节流，只有失败才退避。
    /// </summary>
    private const int SilentFailureBackoffHours = 1;

    /// <summary>
    /// 最近一次<b>失败</b>的静默检查时间戳；静默检查成功即清除。
    /// </summary>
    private const string SilentFailureUtcKey = "Update.SilentFailureUtc";

    /// <summary>
    /// v2.1.2 之前用于「成功即 24 小时不查」节流的键（记录最近一次成功检查时间）。
    /// 现已废弃：新逻辑不读取它，只在首次使用时 best-effort 清理，避免残留失效键。
    /// </summary>
    private const string LegacyThrottleKey = "Update.LastCheckUtc";

    private const string SkippedVersionKey = "Update.SkippedVersion";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>静态复用，避免每次检查都新建 <see cref="HttpClient"/> 造成 socket 耗尽。</summary>
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly ILogger<UpdateService> _logger;
    private readonly ISettingsService _settingsService;

    /// <summary>内存中缓存的「上次失败的静默检查时间」，由 <see cref="GetSilentFailureUtcAsync"/> 懒加载。</summary>
    private DateTimeOffset? _silentFailureUtc;
    private bool _silentFailureStateLoaded;

    /// <summary>废弃节流键的清理只做一次，避免每次检查都去拿设置文件锁。</summary>
    private bool _legacyThrottleKeyCleaned;

    private bool _disposed;

    public UpdateService(ILogger<UpdateService> logger, ISettingsService settingsService)
    {
        _logger = logger;
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public bool IsInstalledBuild => InstalledBuildInfo.IsInstalled();

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return UpdateCheckResult.Failed("更新服务已释放");
        }

        // 只有静默检查受失败退避约束：手动检查永远立即联网，且不读写退避状态。
        if (!manual && await IsInFailureBackoffAsync(cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipped silent update check: the previous attempt failed less than {Hours}h ago",
                SilentFailureBackoffHours);
            return UpdateCheckResult.Skipped();
        }

        try
        {
            using var response = await Http
                .GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await FailAsync(manual, null, DescribeHttpFailure(response)).ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

            var latestVersionText = NormalizeVersionText(release?.TagName);
            if (string.IsNullOrEmpty(latestVersionText))
            {
                return await FailAsync(manual, null, "更新信息缺少版本号").ConfigureAwait(false);
            }

            if (!Version.TryParse(latestVersionText, out _))
            {
                // 无法识别的版本号（例如预发布后缀）不能当作「已是最新」，否则会静默漏掉更新。
                return await FailAsync(manual, null, $"无法识别的版本号: {latestVersionText}").ConfigureAwait(false);
            }

            // 这次检查确实联网并拿到了可信结果 → 清除失败退避标记（手动路径不写盘）。
            await ClearSilentFailureMarkAsync(manual).ConfigureAwait(false);

            if (!IsNewerThanCurrent(latestVersionText))
            {
                _logger.LogDebug("Update check: already up to date (current {Current}, latest {Latest})",
                    VersionInfo.Version, latestVersionText);
                return UpdateCheckResult.UpToDate();
            }

            if (!manual)
            {
                var skippedVersion = await _settingsService
                    .LoadSettingAsync(SkippedVersionKey, string.Empty)
                    .ConfigureAwait(false);

                if (string.Equals(skippedVersion, latestVersionText, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("Update check: version {Version} is on the skip list", latestVersionText);
                    return UpdateCheckResult.Skipped();
                }
            }

            var info = BuildReleaseInfo(release!, latestVersionText);
            _logger.LogInformation("Update available: {Latest} (current {Current}), installer asset: {HasInstaller}",
                info.LatestVersion, VersionInfo.Version, info.HasInstaller);
            return UpdateCheckResult.Available(info);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(manual, null, "网络请求超时，请检查网络连接").ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return await FailAsync(manual, ex, "无法连接到更新服务器，请检查网络连接").ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return await FailAsync(manual, ex, "更新信息解析失败").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await FailAsync(manual, ex, "检查更新时发生未知错误").ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task SkipVersionAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("User skipped update version {Version}", version);
        return _settingsService.SaveSettingAsync(SkippedVersionKey, version);
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
            Timeout = TimeSpan.FromSeconds(10)
        };

        // GitHub API 强制要求 User-Agent，缺失会返回 403。
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SerialPortTool-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>
    /// 静默检查是否处于失败退避窗口内（失败后 <see cref="SilentFailureBackoffHours"/> 小时内不再联网）。
    /// </summary>
    private async Task<bool> IsInFailureBackoffAsync(CancellationToken cancellationToken)
    {
        var lastFailure = await GetSilentFailureUtcAsync(cancellationToken).ConfigureAwait(false);
        return lastFailure != null &&
               DateTimeOffset.UtcNow - lastFailure.Value < TimeSpan.FromHours(SilentFailureBackoffHours);
    }

    /// <summary>
    /// 懒加载「上次失败的静默检查时间」（每个会话只读一次设置）。
    /// </summary>
    /// <remarks>
    /// 时间戳缺失或无法解析时按「没有退避」处理（立即联网），而不是误判为「已被退避」。
    /// </remarks>
    private async Task<DateTimeOffset?> GetSilentFailureUtcAsync(CancellationToken cancellationToken)
    {
        if (!_silentFailureStateLoaded)
        {
            try
            {
                var raw = await _settingsService
                    .LoadSettingAsync(SilentFailureUtcKey, string.Empty)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(raw) &&
                    DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                {
                    _silentFailureUtc = parsed;
                }
            }
            catch (Exception ex)
            {
                // 读取失败不应阻断检查流程（不缓存 = 每次都会联网，但不会误判为「已退避」）。
                _logger.LogDebug(ex, "Failed to read the silent update check failure timestamp");
            }
            finally
            {
                _silentFailureStateLoaded = true;
            }
        }

        await RemoveLegacyThrottleKeyAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return _silentFailureUtc;
    }

    /// <summary>
    /// 静默检查成功后清除失败退避标记。
    /// </summary>
    /// <remarks>
    /// 只有内存中确实存在标记时才写盘：设置写入是整文件 JSON 重写，不能每次启动都无谓重写一遍。
    /// 手动检查（<paramref name="manual"/>）不读写任何退避状态。
    /// </remarks>
    private async Task ClearSilentFailureMarkAsync(bool manual)
    {
        if (manual || _silentFailureUtc == null)
        {
            return;
        }

        _silentFailureUtc = null;
        _silentFailureStateLoaded = true;

        try
        {
            await _settingsService.DeleteSettingAsync(SilentFailureUtcKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 清不掉只影响下一个进程：最坏结果是被多退避一个窗口。
            _logger.LogDebug(ex, "Failed to clear the silent update check failure timestamp");
        }
    }

    /// <summary>
    /// v2.1.2 之前用 <c>Update.LastCheckUtc</c>（最近一次成功检查时间）做 24 小时节流，该键现已废弃。
    /// 首次使用时 best-effort 删除一次；删不掉不影响任何行为（新逻辑不再读取它）。
    /// </summary>
    private async Task RemoveLegacyThrottleKeyAsync()
    {
        if (_legacyThrottleKeyCleaned)
        {
            return;
        }

        _legacyThrottleKeyCleaned = true;

        try
        {
            await _settingsService.DeleteSettingAsync(LegacyThrottleKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove the legacy update throttle timestamp");
        }
    }

    private static UpdateReleaseInfo BuildReleaseInfo(GitHubRelease release, string latestVersionText)
    {
        var asset = FindInstallerAsset(release.Assets);

        return new UpdateReleaseInfo
        {
            LatestVersion = latestVersionText,
            ReleaseNotes = release.Body ?? string.Empty,
            ReleasePageUrl = release.HtmlUrl ?? $"https://github.com/{RepoOwner}/{RepoName}/releases",
            SetupDownloadUrl = asset?.BrowserDownloadUrl,
            SetupSizeBytes = asset?.Size ?? 0,
            PublishedAt = release.PublishedAt ?? DateTimeOffset.MinValue
        };
    }

    /// <summary>
    /// 在 Release 资产中挑选安装程序：文件名以 <c>.exe</c> 结尾且包含 <c>Setup</c>。
    /// 便携 ZIP 不参与自动更新。
    /// </summary>
    private static GitHubAsset? FindInstallerAsset(List<GitHubAsset>? assets)
    {
        if (assets == null)
        {
            return null;
        }

        foreach (var asset in assets)
        {
            if (asset?.Name == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
            {
                continue;
            }

            if (asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                return asset;
            }
        }

        return null;
    }

    private static string DescribeHttpFailure(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            403 or 429 => "更新检查过于频繁（GitHub 接口限流），请稍后再试",
            404 => "未找到发布信息（仓库或版本可能尚未发布）",
            _ => $"更新服务返回 HTTP {status}"
        };
    }

    private static string NormalizeVersionText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        return raw.Trim().TrimStart('v', 'V').Trim();
    }

    /// <summary>
    /// 用 <see cref="Version"/> 做数值比较，判断 <paramref name="candidateText"/> 是否高于当前程序版本。
    /// </summary>
    private static bool IsNewerThanCurrent(string candidateText)
    {
        if (!Version.TryParse(candidateText, out var candidate))
        {
            return false;
        }

        if (!Version.TryParse(VersionInfo.Version, out var current))
        {
            return false;
        }

        return candidate > current;
    }

    /// <summary>
    /// 统一的失败出口：记日志（手动 = Warning，静默 = Debug），并且只在静默路径登记失败退避。
    /// </summary>
    /// <remarks>
    /// 所有失败分支（HTTP 错误、超时、网络异常、解析失败、未知异常）都必须走这里，否则失败退避会漏：
    /// 例如被 GitHub 限流（403/429）后仍随每次启动继续请求，就会形成请求风暴。
    /// </remarks>
    private async Task<UpdateCheckResult> FailAsync(bool manual, Exception? exception, string reason)
    {
        if (manual)
        {
            _logger.LogWarning(exception, "Manual update check failed: {Reason}", reason);
            return UpdateCheckResult.Failed(reason);
        }

        // 静默检查失败只记 Debug，避免断网时刷屏；不得写入完整响应体。
        _logger.LogDebug(exception, "Silent update check failed: {Reason}", reason);

        var now = DateTimeOffset.UtcNow;
        _silentFailureUtc = now;
        _silentFailureStateLoaded = true;

        try
        {
            await _settingsService
                .SaveSettingAsync(SilentFailureUtcKey, now.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (Exception saveEx)
        {
            // 退避时间戳写不进去不能改变本次结果（最坏结果只是下次启动会立即重试）。
            _logger.LogDebug(saveEx, "Failed to persist the silent update check failure timestamp");
        }

        return UpdateCheckResult.Failed(reason);
    }
}

/// <summary>
/// GitHub Releases API（<c>releases/latest</c>）响应的最小投影。
/// 使用文件级 internal 类型（而非私有嵌套类型）以避开 System.Text.Json 对不可见类型的反序列化限制。
/// </summary>
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

/// <summary>
/// GitHub Release 资产（<c>assets[]</c>）的最小投影。
/// </summary>
internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
