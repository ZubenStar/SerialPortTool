using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Services;

/// <summary>
/// 设置服务实现
/// </summary>
/// <remarks>
/// <para>
/// 写入路径采用 in-memory 缓存 + 500ms 防抖定时器。
/// 修复了之前每次属性变化（包括发送框逐键、搜索历史每次插入、tuning 路径每次切换等）
/// 都触发整个 JSON 文件 read→deserialize→mutate→serialize→write 的卡顿问题。
/// 关窗或 DI Dispose 时通过 DisposeAsync → FlushAsync 把尚未落盘的变更刷出。
/// _fileLock 仍然在所有实际 I/O 段内使用，保留 v1.8.10 引入的并发保护。
/// </para>
/// <para>
/// 写盘是<b>原子</b>的：先写同目录临时文件，再 <c>File.Move(overwrite: true)</c> 顶替原文件。原来直接
/// <c>File.WriteAllText</c> 覆盖，断电 / 崩溃 / 磁盘满都会留下半截 JSON；而下一个进程启动时读到半截
/// JSON 会「解析失败 → 空字典 → 立刻写回空文件」，把一次写入事故放大成用户的端口配置、外观、颜色、
/// 搜索历史全部永久丢失。现在这两半都堵上了：解析失败进入只读保护，绝不写盘。
/// </para>
/// </remarks>
public class SettingsService : ISettingsService, IAsyncDisposable
{
    private const int FlushDelayMs = 500;

    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFile;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly Timer _flushTimer;

    private Dictionary<string, object>? _cache;
    private bool _dirty;
    private bool _disposed;

    // Set when an existing settings file could not be read/parsed. Every write path checks it: the
    // in-memory cache is not authoritative in that state, so persisting it would replace the user's
    // real settings with an empty document.
    private bool _loadFailed;
    private bool _loadFailureReported;

    public event EventHandler? SettingsLoadFailed;

    public bool HasLoadFailure => _loadFailed;

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;

        // Store settings in user's AppData folder
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsDir = Path.Combine(appDataPath, "SerialPortTool");
        Directory.CreateDirectory(settingsDir);

        _settingsFile = Path.Combine(settingsDir, "settings.json");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        _flushTimer = new Timer(OnFlushTimerTick, null, Timeout.Infinite, Timeout.Infinite);

        _logger.LogInformation("SettingsService initialized. Settings file: {SettingsFile}", _settingsFile);
    }

    public async Task SaveSettingAsync(string key, int value)
    {
        await SaveSettingInternalAsync(key, value);
    }

    public async Task<int> LoadSettingAsync(string key, int defaultValue)
    {
        var value = await LoadSettingInternalAsync(key);
        if (value != null && int.TryParse(value.ToString(), out var intValue))
        {
            return intValue;
        }
        return defaultValue;
    }

    public async Task SaveSettingAsync(string key, string value)
    {
        await SaveSettingInternalAsync(key, value);
    }

    public async Task<string> LoadSettingAsync(string key, string defaultValue)
    {
        var value = await LoadSettingInternalAsync(key);
        return value?.ToString() ?? defaultValue;
    }

    public async Task DeleteSettingAsync(string key)
    {
        await _fileLock.WaitAsync();
        try
        {
            await EnsureCacheLoadedLocked();

            // Never rewrite the file while in the read-only protection state: the cache is not the
            // user's real settings, so "delete one key" would silently delete all of them.
            if (_loadFailed)
            {
                ReportLoadFailureLocked();
                return;
            }

            if (_cache!.TryGetValue(key, out var removedValue))
            {
                // Delete is a user-visible action — flush immediately rather than waiting for debounce.
                _cache.Remove(key);
                if (await TrySaveAllSettingsAsync(_cache))
                {
                    _dirty = false;
                    _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _logger.LogInformation("Deleted setting: {Key}", key);
                }
                else
                {
                    // The file still holds the key (or could not be touched at all) — keep the cache
                    // consistent with it rather than reporting a delete that never happened.
                    _cache[key] = removedValue;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting setting {Key}", key);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task ClearAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _cache = new Dictionary<string, object>();
            _dirty = false;

            if (File.Exists(_settingsFile))
            {
                File.Delete(_settingsFile);
                _logger.LogInformation("Cleared all settings");
            }

            // An explicit "clear everything" is the user's way out of a corrupt file: once it is
            // gone, an empty cache *is* the truth, so the read-only protection is lifted. Kept after
            // the delete on purpose — if the delete fails, the file is still there and still corrupt,
            // so the protection must stay.
            _loadFailed = false;
            _loadFailureReported = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing settings");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task FlushAsync()
    {
        // _disposed is checked here so a timer callback racing with DisposeAsync doesn't try to
        // re-enter _fileLock after it has been released by Dispose.
        if (_disposed)
        {
            return;
        }

        await _fileLock.WaitAsync();
        try
        {
            if (_cache == null || !_dirty)
            {
                return;
            }

            if (_loadFailed)
            {
                // Stop the debounce loop rather than retrying forever, and tell the user once.
                _dirty = false;
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                ReportLoadFailureLocked();
                return;
            }

            if (await TrySaveAllSettingsAsync(_cache))
            {
                _dirty = false;
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _logger.LogDebug("Flushed pending settings to disk");
            }
            else
            {
                // Keep _dirty set so a later flush (or the shutdown flush) can retry, but don't spin:
                // SaveAllSettingsAsync has already logged the reason. Re-arm the debounce timer only
                // for transient failures would create a retry storm on a locked/denied file, so the
                // timer stays off and the retry happens on the next explicit Save/Flush.
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush settings to disk");
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            // Best-effort
        }

        // Flush BEFORE setting _disposed so the call goes through; FlushAsync's _disposed guard
        // exists to protect timer-driven callbacks racing with this teardown.
        await FlushAsync();

        _disposed = true;

        try
        {
            _flushTimer.Dispose();
        }
        catch
        {
            // Best-effort
        }

        _fileLock.Dispose();
    }

    private async Task SaveSettingInternalAsync(string key, object value)
    {
        await _fileLock.WaitAsync();
        try
        {
            await EnsureCacheLoadedLocked();
            _cache![key] = value;

            if (_loadFailed)
            {
                // The value stays in memory so the running session behaves as the user expects, but it
                // is never persisted: the file on disk still holds their real settings, and flushing
                // this cache would replace them with an empty document. Report once and stop here.
                ReportLoadFailureLocked();
                return;
            }

            _dirty = true;
            _flushTimer.Change(FlushDelayMs, Timeout.Infinite);
            _logger.LogTrace("Cached setting: {Key} = {Value}", key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error caching setting {Key}", key);
            throw;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<object?> LoadSettingInternalAsync(string key)
    {
        await _fileLock.WaitAsync();
        try
        {
            await EnsureCacheLoadedLocked();
            return _cache!.TryGetValue(key, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading setting {Key}", key);
            return null;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private void OnFlushTimerTick(object? state)
    {
        // Fire-and-forget by design — exceptions are logged inside FlushAsync.
        _ = FlushAsync();
    }

    /// <summary>
    /// Lazily populate the in-memory cache from disk on first access.
    /// Caller MUST already hold _fileLock.
    /// </summary>
    private async Task EnsureCacheLoadedLocked()
    {
        if (_cache != null)
        {
            return;
        }

        var (settings, succeeded) = await LoadAllSettingsAsync();
        if (!succeeded)
        {
            // Deliberately report it here rather than letting the failure stay invisible until the
            // first flush decides to write the (empty) cache over the user's file.
            _loadFailed = true;
        }

        _cache = settings;
    }

    /// <summary>
    /// Reads the settings file. The returned flag is <c>false</c> only when the file exists but could
    /// not be read or parsed — a missing file is a normal first run.
    /// </summary>
    private async Task<(Dictionary<string, object> Settings, bool Succeeded)> LoadAllSettingsAsync()
    {
        if (!File.Exists(_settingsFile))
        {
            return (new Dictionary<string, object>(), true);
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            if (string.IsNullOrWhiteSpace(json))
            {
                // A zero-byte / whitespace-only file is the classic half-written result of a
                // non-atomic write. Treat it as a failure so it is never "parsed" as empty settings.
                _logger.LogError("Settings file is empty and will not be treated as valid: {SettingsFile}", _settingsFile);
                return (new Dictionary<string, object>(), false);
            }

            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            var result = new Dictionary<string, object>();
            if (settings != null)
            {
                foreach (var kvp in settings)
                {
                    result[kvp.Key] = kvp.Value.ValueKind switch
                    {
                        // TryGetInt32 first: GetInt32() throws on a non-integral number, which the
                        // catch below would turn into "the whole file is unreadable" — and that now
                        // means the read-only protection locks out every future write. Nothing in the
                        // app writes a non-integral number today, but a hand-edited file should not be
                        // able to wedge saving forever.
                        JsonValueKind.Number when kvp.Value.TryGetInt32(out var number) => number,
                        JsonValueKind.Number => kvp.Value.GetDouble(),
                        JsonValueKind.String => kvp.Value.GetString() ?? string.Empty,
                        _ => kvp.Value.ToString()
                    };
                }
            }
            return (result, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings file {SettingsFile}", _settingsFile);
            return (new Dictionary<string, object>(), false);
        }
    }

    /// <summary>
    /// Serializes <paramref name="settings"/> and swaps it in atomically.
    /// </summary>
    /// <returns><c>true</c> when the file on disk now holds <paramref name="settings"/>.</returns>
    /// <remarks>
    /// Write-to-temp-then-rename, so a crash, power loss or full disk can never leave a truncated
    /// <c>settings.json</c> behind: the destination is either the old file or the complete new one.
    /// The temp file lives in the same directory (a same-volume rename is what makes this atomic).
    /// </remarks>
    private async Task<bool> TrySaveAllSettingsAsync(Dictionary<string, object> settings)
    {
        var tempFile = _settingsFile + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(tempFile, json);
            File.Move(tempFile, _settingsFile, overwrite: true);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            // Read-only target (policy, antivirus, another process holding it). The original file is
            // untouched — that is the whole point of writing to a temp name first.
            _logger.LogError(ex, "Access denied writing settings file {SettingsFile}; original left unchanged", _settingsFile);
            return false;
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "I/O error writing settings file {SettingsFile}; original left unchanged", _settingsFile);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings file {SettingsFile}", _settingsFile);
            return false;
        }
        finally
        {
            TryDeleteTempFile(tempFile);
        }
    }

    private void TryDeleteTempFile(string tempFile)
    {
        try
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the settings temp file {TempFile}", tempFile);
        }
    }

    /// <summary>
    /// Logs (once) and announces the read-only protection state. Caller holds <see cref="_fileLock"/>.
    /// </summary>
    private void ReportLoadFailureLocked()
    {
        if (_loadFailureReported)
        {
            return;
        }

        _loadFailureReported = true;
        _logger.LogError(
            "Settings are read-only for this session: {SettingsFile} could not be read, so writing would destroy it. " +
            "Fix or delete the file and restart.",
            _settingsFile);

        try
        {
            SettingsLoadFailed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A SettingsLoadFailed subscriber threw");
        }
    }
}
