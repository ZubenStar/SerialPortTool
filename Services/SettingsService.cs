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
/// 写入路径采用 in-memory 缓存 + 500ms 防抖定时器。
/// 修复了之前每次属性变化（包括发送框逐键、搜索历史每次插入、tuning 路径每次切换等）
/// 都触发整个 JSON 文件 read→deserialize→mutate→serialize→write 的卡顿问题。
/// 关窗或 DI Dispose 时通过 DisposeAsync → FlushAsync 把尚未落盘的变更刷出。
/// _fileLock 仍然在所有实际 I/O 段内使用，保留 v1.8.10 引入的并发保护。
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
            if (_cache!.Remove(key))
            {
                // Delete is a user-visible action — flush immediately rather than waiting for debounce.
                await SaveAllSettingsAsync(_cache);
                _dirty = false;
                _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _logger.LogInformation("Deleted setting: {Key}", key);
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

            await SaveAllSettingsAsync(_cache);
            _dirty = false;
            _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _logger.LogDebug("Flushed pending settings to disk");
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
        _cache = await LoadAllSettingsAsync();
    }

    private async Task<Dictionary<string, object>> LoadAllSettingsAsync()
    {
        if (!File.Exists(_settingsFile))
        {
            return new Dictionary<string, object>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

            var result = new Dictionary<string, object>();
            if (settings != null)
            {
                foreach (var kvp in settings)
                {
                    result[kvp.Key] = kvp.Value.ValueKind switch
                    {
                        JsonValueKind.Number => kvp.Value.GetInt32(),
                        JsonValueKind.String => kvp.Value.GetString() ?? string.Empty,
                        _ => kvp.Value.ToString()
                    };
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings file");
            return new Dictionary<string, object>();
        }
    }

    private async Task SaveAllSettingsAsync(Dictionary<string, object> settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await File.WriteAllTextAsync(_settingsFile, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings file");
            throw;
        }
    }
}
