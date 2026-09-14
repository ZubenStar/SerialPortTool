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
/// <item>匿名调用限流 60 次/小时，因此静默检查必须走「上次检查时间」缓存，这是可靠性要求而非优化。</item>
/// <item>静默检查失败必须静默（Debug 日志），不得在断网时产生错误风暴。</item>
/// </list>
/// </remarks>
public sealed class UpdateService : IUpdateService
{
    private const string RepoOwner = "ZubenStar";
    private const string RepoName = "SerialPortTool";

    private const string LatestReleaseApiUrl =
        "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

    /// <summary>静默检查的缓存时长（小时）。</summary>
    private const int SilentCheckCacheHours = 24;

    private const string LastCheckKey = "Update.LastCheckUtc";
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

    /// <summary>内存中缓存的「上次成功检查时间」，由 <see cref="IsRecentCheckAsync"/> 懒加载。</summary>
    private DateTimeOffset? _lastCheckUtc;
    private bool _lastCheckLoaded;

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

        if (!manual &&
            await IsRecentCheckAsync(SilentCheckCacheHours, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipped silent update check: last check was less than {Hours}h ago", SilentCheckCacheHours);
            return UpdateCheckResult.Skipped();
        }

        try
        {
            using var response = await Http
                .GetAsync(LatestReleaseApiUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var reason = DescribeHttpFailure(response);
                LogFailure(manual, null, reason);
                return UpdateCheckResult.Failed(reason);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

            var latestVersionText = NormalizeVersionText(release?.TagName);
            if (string.IsNullOrEmpty(latestVersionText))
            {
                const string emptyReason = "更新信息缺少版本号";
                LogFailure(manual, null, emptyReason);
                return UpdateCheckResult.Failed(emptyReason);
            }

            if (!Version.TryParse(latestVersionText, out _))
            {
                // 无法识别的版本号（例如预发布后缀）不能当作「已是最新」，否则会静默漏掉更新。
                var badVersionReason = $"无法识别的版本号: {latestVersionText}";
                LogFailure(manual, null, badVersionReason);
                return UpdateCheckResult.Failed(badVersionReason);
            }

            // 检查成功才刷新「上次检查时间」，失败时保留旧值以便下次重试。
            await MarkCheckedAsync().ConfigureAwait(false);

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
            const string timeoutReason = "网络请求超时，请检查网络连接";
            LogFailure(manual, null, timeoutReason);
            return UpdateCheckResult.Failed(timeoutReason);
        }
        catch (HttpRequestException ex)
        {
            const string networkReason = "无法连接到更新服务器，请检查网络连接";
            LogFailure(manual, ex, networkReason);
            return UpdateCheckResult.Failed(networkReason);
        }
        catch (JsonException ex)
        {
            const string parseReason = "更新信息解析失败";
            LogFailure(manual, ex, parseReason);
            return UpdateCheckResult.Failed(parseReason);
        }
        catch (Exception ex)
        {
            const string unknownReason = "检查更新时发生未知错误";
            LogFailure(manual, ex, unknownReason);
            return UpdateCheckResult.Failed(unknownReason);
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
    public async Task<bool> IsRecentCheckAsync(int withinHours, CancellationToken cancellationToken = default)
    {
        if (withinHours <= 0)
        {
            return false;
        }

        await EnsureLastCheckLoadedAsync(cancellationToken).ConfigureAwait(false);

        var lastCheck = _lastCheckUtc;
        return lastCheck != null && DateTimeOffset.UtcNow - lastCheck.Value < TimeSpan.FromHours(withinHours);
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

    private Task MarkCheckedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        _lastCheckUtc = now;
        _lastCheckLoaded = true;
        return _settingsService.SaveSettingAsync(LastCheckKey, now.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 懒加载「上次成功检查时间」（每个会话只读一次设置）。
    /// </summary>
    private async Task EnsureLastCheckLoadedAsync(CancellationToken cancellationToken)
    {
        if (_lastCheckLoaded)
        {
            return;
        }

        try
        {
            var raw = await _settingsService
                .LoadSettingAsync(LastCheckKey, string.Empty)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                _lastCheckUtc = parsed;
            }
        }
        catch (Exception ex)
        {
            // 缓存读取失败不应阻断检查流程（不缓存 = 每次都会联网，但不会误判为「已检查」）。
            _logger.LogDebug(ex, "Failed to read last update check timestamp");
        }
        finally
        {
            _lastCheckLoaded = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private void LogFailure(bool manual, Exception? exception, string reason)
    {
        if (manual)
        {
            _logger.LogWarning(exception, "Manual update check failed: {Reason}", reason);
        }
        else
        {
            // 静默检查失败只记 Debug，避免断网时刷屏；不得写入完整响应体。
            _logger.LogDebug(exception, "Silent update check failed: {Reason}", reason);
        }
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
