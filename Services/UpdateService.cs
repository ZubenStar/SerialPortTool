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
/// <item><b>一次检查发现更新后就不再重复检查</b>：结果存进 <see cref="PendingUpdateKey"/>，之后的静默检查
/// 直接复用它，一个请求都不发。「有更新」这个事实不会自己消失，而用户没升级之前的每一次复查
/// 都是在白白消耗匿名配额——「稍后」之后重启程序必须立刻又能看到提示，而不是再等一个节流窗口。
/// 但缓存不是永久的：超过 <see cref="PendingUpdateMaxAgeHours"/> 小时就作废、重新联网确认一次，
/// 这样既不会拿着一个已经过时（甚至已被撤回）的版本无限期提示，也不会退化成「再也不查」。</item>
/// <item>两次<b>成功</b>的静默检查之间至少间隔 <see cref="SilentSuccessCooldownMinutes"/> 分钟：新版本最坏只被推迟
/// 这么久就一定会被发现，而启动频次直接换算成的请求量被压到匿名接口上限（60 次/小时）的 1/30 以下。
/// 历史上这里是「每次启动都联网」（v2.1.2）——不限速换来的那点即时性会撞上限流（403），
/// 反而让检查整体不可用，因此不能再回到「成功不节流」。这条规则只在<b>没有</b>待处理更新时生效。</item>
/// <item><b>失败必须退避</b>（<see cref="SilentFailureBackoffHours"/> 小时，含被限流 403/429），
/// 否则断网或限流时会随每次启动变成请求风暴——这是可靠性要求而非优化。</item>
/// <item>手动检查（<c>manual: true</c>）始终立即联网，不读写任何节流键（<b>但会刷新待处理更新缓存</b>，
/// 那是结果缓存而非节奏状态）。</item>
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
    /// 静默检查失败后的退避时长（小时）。失败一律退避，成功只走
    /// <see cref="SilentSuccessCooldownMinutes"/> 的短冷却。
    /// </summary>
    /// <remarks>
    /// GitHub 匿名接口的上限是 60 次/小时，且按<b>公网 IP</b> 计（NAT / 代理后可能与他人共享），
    /// 而主限流窗口最长 1 小时、二级限流的 <c>Retry-After</c> 通常只有 1 分钟，
    /// 所以 1 小时足以覆盖整个限流窗口——再长只会拖慢恢复，再短则会在窗口内空转。
    /// </remarks>
    private const int SilentFailureBackoffHours = 1;

    /// <summary>
    /// 两次<b>成功</b>的静默检查之间的最短间隔（分钟）。
    /// </summary>
    /// <remarks>
    /// 「每次启动都联网」把启动次数直接换算成请求数，反复启动（开发调试、静默更新后的自动重启）
    /// 或与他人的 GitHub 请求共享同一公网 IP 时会撞上匿名接口的 60 次/小时上限，表现为
    /// 「更新检查过于频繁（GitHub 接口限流）」——即使用户只启动了几次程序。
    /// 30 分钟把单机请求压到 2 次/小时（上限的 1/30），代价是新版本最坏在发布后半小时内才被发现，
    /// 而「帮助 → 检查更新」的手动路径始终立即联网，需要即时确认时不受影响。
    /// </remarks>
    private const int SilentSuccessCooldownMinutes = 30;

    /// <summary>
    /// 最近一次<b>失败</b>的静默检查时间戳；静默检查成功即清除。
    /// </summary>
    private const string SilentFailureUtcKey = "Update.SilentFailureUtc";

    /// <summary>
    /// 最近一次<b>成功</b>的静默检查时间戳，用作下一次静默检查的最早时间。
    /// </summary>
    private const string SilentSuccessUtcKey = "Update.SilentSuccessUtc";

    /// <summary>
    /// 待处理更新缓存的有效期（小时）：超过就作废，让这次静默检查重新联网确认一次。
    /// </summary>
    /// <remarks>
    /// 一天是「不重复请求」和「不拿着旧结论」之间的折中：用户点「稍后」之后当天重启多少次都还是立刻看到
    /// 同一个提示，而第二天起会重新要一次权威答案——期间若发布了更高版本、或这一版被撤回，
    /// 最迟一天内就会被纠正。作废时缓存会被删除，所以断网期间不会拿着过期结论反复提示。
    /// </remarks>
    private const int PendingUpdateMaxAgeHours = 24;

    /// <summary>
    /// 最近一次检查发现的、用户尚未处理的更新（<see cref="UpdateReleaseInfo"/> 的 JSON 序列化）。
    /// </summary>
    /// <remarks>
    /// 只要这个缓存还在（版本仍高于当前程序版本，且写入时间不超过
    /// <see cref="PendingUpdateMaxAgeHours"/> 小时），静默检查就直接把它当作结果返回、<b>完全不联网</b>：
    /// 一次检查已经回答了「有没有更新」，再问一次 GitHub 不可能得到别的答案，只会在用户点「稍后」之后
    /// 反复消耗 60 次/小时的匿名配额。缓存由三条路径失效——用户点了「跳过此版本」
    /// （<see cref="SkipVersionAsync"/>）、装上的新版本已经不再低于它、或它活过了
    /// <see cref="PendingUpdateMaxAgeHours"/>（后两者都在下次读取时判定并丢弃）。
    /// </remarks>
    private const string PendingUpdateKey = "Update.PendingRelease";

    /// <summary>
    /// <see cref="PendingUpdateKey"/> 的写入时间，用于判定缓存是否已超过
    /// <see cref="PendingUpdateMaxAgeHours"/> 小时。
    /// </summary>
    /// <remarks>
    /// 时间戳缺失或无法解析按「已作废」处理——宁可多问一次 GitHub，也不要拿着一份来路不明年龄的
    /// 结论无限期提示。与 <see cref="PendingUpdateKey"/> 分成两个键是为了不动已存的缓存内容：
    /// 两者万一写崩一个，最坏结果也只是多做一次请求。
    /// </remarks>
    private const string PendingUpdateUtcKey = "Update.PendingReleaseUtc";

    /// <summary>
    /// v2.1.2 之前用于「成功即 24 小时不查」节流的键（记录最近一次成功检查时间）。
    /// 现已废弃：新逻辑不读取它（改用窗口短得多的 <see cref="SilentSuccessUtcKey"/>），
    /// 只在首次使用时 best-effort 清理，避免残留失效键。
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

    /// <summary>内存中缓存的「上次失败的静默检查时间」，由 <see cref="EnsureThrottleStateLoadedAsync"/> 懒加载。</summary>
    private DateTimeOffset? _silentFailureUtc;

    /// <summary>内存中缓存的「上次成功的静默检查时间」，同样懒加载；只在静默成功时写盘。</summary>
    private DateTimeOffset? _silentSuccessUtc;

    /// <summary>两个节流时间戳共用一次懒加载。</summary>
    private bool _throttleStateLoaded;

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

        // 手动检查永远立即联网（它是缓存可能过期时的唯一出口）；静默检查先看缓存、再谈节流。
        if (!manual)
        {
            var pending = await GetPendingUpdateAsync().ConfigureAwait(false);
            if (pending != null)
            {
                _logger.LogDebug("Update check: reusing the cached pending update {Version}", pending.LatestVersion);
                return UpdateCheckResult.Available(pending);
            }

            if (await IsSilentCheckThrottledAsync(cancellationToken).ConfigureAwait(false))
            {
                return UpdateCheckResult.Skipped();
            }
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

            // 这次检查确实联网并拿到了可信结果 → 清除失败退避标记并登记成功时间（手动路径不写盘）。
            await ClearSilentFailureMarkAsync(manual).ConfigureAwait(false);
            await MarkSilentCheckSucceededAsync(manual).ConfigureAwait(false);

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

            // 记下这次发现：在用户升级或明确跳过之前，后续静默检查直接读它，不再联网。
            await SavePendingUpdateAsync(info).ConfigureAwait(false);

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
    public async Task SkipVersionAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        _logger.LogInformation("User skipped update version {Version}", version);
        await _settingsService.SaveSettingAsync(SkippedVersionKey, version).ConfigureAwait(false);

        // 待处理更新缓存里存的就是这个版本：不清掉的话，下次静默检查会直接从缓存里把它端出来，
        // 「跳过」就成了空话。
        await ClearPendingUpdateAsync("the user skipped it").ConfigureAwait(false);
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
    /// 静默检查当前是否应被跳过：处于失败退避窗口内，或距上次成功检查不足
    /// <see cref="SilentSuccessCooldownMinutes"/> 分钟。
    /// </summary>
    /// <remarks>
    /// 两条规则都不改变用户可见结果——命中时返回 <see cref="UpdateCheckStatus.Skipped"/>，
    /// 调用方静默忽略。手动检查不经过这里。
    /// </remarks>
    private async Task<bool> IsSilentCheckThrottledAsync(CancellationToken cancellationToken)
    {
        await EnsureThrottleStateLoadedAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (_silentFailureUtc is { } lastFailure &&
            now - lastFailure < TimeSpan.FromHours(SilentFailureBackoffHours))
        {
            _logger.LogDebug("Skipped silent update check: the previous attempt failed less than {Hours}h ago",
                SilentFailureBackoffHours);
            return true;
        }

        if (_silentSuccessUtc is { } lastSuccess &&
            now - lastSuccess < TimeSpan.FromMinutes(SilentSuccessCooldownMinutes))
        {
            _logger.LogDebug(
                "Skipped silent update check: the previous attempt succeeded less than {Minutes}min ago",
                SilentSuccessCooldownMinutes);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 懒加载两个节流时间戳（失败退避 + 成功冷却，每个会话只读一次设置）。
    /// </summary>
    /// <remarks>
    /// 时间戳缺失或无法解析时按「没有节流」处理（立即联网），而不是误判为「已被节流」：
    /// 漏一次请求是可接受的，静默地不再检查更新不是。
    /// </remarks>
    private async Task EnsureThrottleStateLoadedAsync(CancellationToken cancellationToken)
    {
        if (!_throttleStateLoaded)
        {
            try
            {
                _silentFailureUtc = await ReadTimestampAsync(SilentFailureUtcKey).ConfigureAwait(false);
                _silentSuccessUtc = await ReadTimestampAsync(SilentSuccessUtcKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 读取失败不应阻断检查流程（不缓存 = 每次都会联网，但不会误判为「已节流」）。
                _logger.LogDebug(ex, "Failed to read the silent update check throttle timestamps");
            }
            finally
            {
                _throttleStateLoaded = true;
            }
        }

        await RemoveLegacyThrottleKeyAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>读取一个 ISO 8601 时间戳设置；缺失或无法解析时返回 <c>null</c>。</summary>
    private async Task<DateTimeOffset?> ReadTimestampAsync(string key)
    {
        var raw = await _settingsService.LoadSettingAsync(key, string.Empty).ConfigureAwait(false);

        return !string.IsNullOrWhiteSpace(raw) &&
               DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// 静默检查成功后清除失败退避标记。
    /// </summary>
    /// <remarks>
    /// 只有内存中确实存在标记时才写盘：设置写入是整文件 JSON 重写，不能每次启动都无谓重写一遍。
    /// 手动检查（<paramref name="manual"/>）不读写任何节流状态。
    /// </remarks>
    private async Task ClearSilentFailureMarkAsync(bool manual)
    {
        if (manual || _silentFailureUtc == null)
        {
            return;
        }

        _silentFailureUtc = null;
        _throttleStateLoaded = true;

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
    /// 登记「最近一次成功的静默检查时间」，把下一次静默检查推迟
    /// <see cref="SilentSuccessCooldownMinutes"/> 分钟。
    /// </summary>
    /// <remarks>
    /// 手动检查（<paramref name="manual"/>）不写盘：用户主动查过之后不该连带改动静默检查的节奏。
    /// 写盘失败只影响下一个进程（最坏结果是下次启动立刻再查一次），不能改变本次结果。
    /// </remarks>
    private async Task MarkSilentCheckSucceededAsync(bool manual)
    {
        if (manual)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _silentSuccessUtc = now;
        _throttleStateLoaded = true;

        try
        {
            await _settingsService
                .SaveSettingAsync(SilentSuccessUtcKey, now.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist the silent update check success timestamp");
        }
    }

    /// <summary>
    /// 读取待处理更新缓存；没有、无法解析、已不再高于当前程序版本（用户已经装上了）、
    /// 或已超过 <see cref="PendingUpdateMaxAgeHours"/> 小时时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 读不出来、或已经过期时一律当作「没有缓存」继续走正常检查流程——缓存是省流量的优化，
    /// 不能变成漏掉更新的原因。作废时顺手删掉，免得它一直躺在设置文件里被反复读取和丢弃。
    /// </remarks>
    private async Task<UpdateReleaseInfo?> GetPendingUpdateAsync()
    {
        try
        {
            var raw = await _settingsService.LoadSettingAsync(PendingUpdateKey, string.Empty).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var info = JsonSerializer.Deserialize<UpdateReleaseInfo>(raw, JsonOptions);
            if (info == null || string.IsNullOrWhiteSpace(info.LatestVersion))
            {
                return null;
            }

            if (!IsNewerThanCurrent(info.LatestVersion))
            {
                // 用户已经把这一版装上了（自动更新后重启的第一个进程就是这样）：丢弃这个陈旧的缓存。
                await ClearPendingUpdateAsync($"version {info.LatestVersion} is no longer newer than the current build")
                    .ConfigureAwait(false);
                return null;
            }

            var cachedAt = await ReadTimestampAsync(PendingUpdateUtcKey).ConfigureAwait(false);
            if (cachedAt == null ||
                DateTimeOffset.UtcNow - cachedAt.Value >= TimeSpan.FromHours(PendingUpdateMaxAgeHours))
            {
                // 缓存活过了一天：不再直接采信，这次检查重新联网确认一次。
                // 确认到同样的版本会把它按新的时间戳写回，确认不到就说明它已经不是最新了。
                _logger.LogDebug(
                    "Update check: the cached pending update {Version} is over {Hours}h old; verifying against GitHub",
                    info.LatestVersion, PendingUpdateMaxAgeHours);
                await ClearPendingUpdateAsync($"it lived longer than {PendingUpdateMaxAgeHours}h")
                    .ConfigureAwait(false);
                return null;
            }

            return info;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to use the pending update cache");
            return null;
        }
    }

    /// <summary>
    /// 记录一次发现，供后续静默检查复用（不联网）。
    /// </summary>
    /// <remarks>
    /// 内容与写入时间必须一起写：只留下内容的话，下一次读取会因为它没有时间戳而直接判为过期。
    /// </remarks>
    private async Task SavePendingUpdateAsync(UpdateReleaseInfo info)
    {
        var now = DateTimeOffset.UtcNow;

        try
        {
            var json = JsonSerializer.Serialize(info, JsonOptions);
            await _settingsService.SaveSettingAsync(PendingUpdateKey, json).ConfigureAwait(false);
            await _settingsService
                .SaveSettingAsync(PendingUpdateUtcKey, now.ToString("O", CultureInfo.InvariantCulture))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 写不进去只影响下一个进程：它会重新联网查一次。
            _logger.LogDebug(ex, "Failed to persist the pending update cache");
        }
    }

    /// <summary>
    /// 丢弃待处理更新缓存：用户跳过该版本、该版本已经被装上、或它已经超过
    /// <see cref="PendingUpdateMaxAgeHours"/> 小时。
    /// </summary>
    private async Task ClearPendingUpdateAsync(string reason)
    {
        try
        {
            await _settingsService.DeleteSettingAsync(PendingUpdateKey).ConfigureAwait(false);
            await _settingsService.DeleteSettingAsync(PendingUpdateUtcKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 删不掉最坏只是多提示一次；静默路径不得因此失败。
            _logger.LogDebug(ex, "Failed to clear the pending update cache ({Reason})", reason);
        }
    }

    /// <summary>
    /// v2.1.2 之前用 <c>Update.LastCheckUtc</c>（最近一次成功检查时间）做 24 小时节流，该键现已废弃：
    /// 成功侧改由窗口短得多的 <see cref="SilentSuccessUtcKey"/> 承担。
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
        _throttleStateLoaded = true;

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
