using System;
using System.Text.Json.Serialization;
using SerialPortTool.Core.Enums;

namespace SerialPortTool.Models;

/// <summary>
/// 一次更新检查解析出的发布信息。
/// 字段与 GitHub Releases API（<c>releases/latest</c>）的
/// <c>tag_name</c> / <c>body</c> / <c>html_url</c> / <c>published_at</c> /
/// <c>assets[].name</c> / <c>assets[].browser_download_url</c> / <c>assets[].size</c> 一一对应。
/// </summary>
public sealed class UpdateReleaseInfo
{
    /// <summary>
    /// 最新版本号（已去掉前导 <c>v</c>），例如 <c>1.8.14</c>
    /// </summary>
    public string LatestVersion { get; set; } = string.Empty;

    /// <summary>
    /// 更新说明正文（Release body）
    /// </summary>
    public string ReleaseNotes { get; set; } = string.Empty;

    /// <summary>
    /// Release 页面地址，供「前往下载页」使用
    /// </summary>
    public string ReleasePageUrl { get; set; } = string.Empty;

    /// <summary>
    /// 自动更新所使用的安装程序（Setup.exe）下载地址；为空表示该 Release 未提供安装包
    /// </summary>
    public string? SetupDownloadUrl { get; set; }

    /// <summary>
    /// 安装程序字节数（来自 Release asset 的 <c>size</c>），用于下载后校验；未知时为 0
    /// </summary>
    public long SetupSizeBytes { get; set; }

    /// <summary>
    /// 发布时间（解析失败时为 <see cref="DateTimeOffset.MinValue"/>）
    /// </summary>
    public DateTimeOffset PublishedAt { get; set; }

    /// <summary>
    /// 是否提供可用于自动更新的安装包
    /// </summary>
    /// <remarks>
    /// 派生自 <see cref="SetupDownloadUrl"/>，因此不参与序列化：待处理更新缓存
    /// （<c>Update.PendingRelease</c>）存的就是这个类型，写进去只会是一份可能过期的副本。
    /// </remarks>
    [JsonIgnore]
    public bool HasInstaller => !string.IsNullOrWhiteSpace(SetupDownloadUrl);
}

/// <summary>
/// 更新检查结果。
/// </summary>
public sealed class UpdateCheckResult
{
    /// <summary>
    /// 检查状态
    /// </summary>
    public UpdateCheckStatus Status { get; set; }

    /// <summary>
    /// 发现的发布信息；仅在 <see cref="UpdateCheckStatus.UpdateAvailable"/> 时非空
    /// </summary>
    public UpdateReleaseInfo? Info { get; set; }

    /// <summary>
    /// 失败原因（面向用户的可读文案）；仅在 <see cref="UpdateCheckStatus.Failed"/> 时非空
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// 构造「已是最新」结果
    /// </summary>
    public static UpdateCheckResult UpToDate() => new() { Status = UpdateCheckStatus.UpToDate };

    /// <summary>
    /// 构造「发现新版本」结果
    /// </summary>
    public static UpdateCheckResult Available(UpdateReleaseInfo info) =>
        new() { Status = UpdateCheckStatus.UpdateAvailable, Info = info };

    /// <summary>
    /// 构造「检查失败」结果
    /// </summary>
    public static UpdateCheckResult Failed(string reason) =>
        new() { Status = UpdateCheckStatus.Failed, FailureReason = reason };

    /// <summary>
    /// 构造「本次跳过」结果
    /// </summary>
    public static UpdateCheckResult Skipped() => new() { Status = UpdateCheckStatus.Skipped };
}
