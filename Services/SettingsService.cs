using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SerialPortTool.Services;

/// <summary>
/// 设置服务实现
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsFile;
    private readonly JsonSerializerOptions _jsonOptions;

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
        try
        {
            var settings = await LoadAllSettingsAsync();
            if (settings.ContainsKey(key))
            {
                settings.Remove(key);
                await SaveAllSettingsAsync(settings);
                _logger.LogInformation("Deleted setting: {Key}", key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting setting {Key}", key);
        }
    }

    public async Task ClearAsync()
    {
        try
        {
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
        await Task.CompletedTask;
    }

    private async Task SaveSettingInternalAsync(string key, object value)
    {
        try
        {
            var settings = await LoadAllSettingsAsync();
            settings[key] = value;
            await SaveAllSettingsAsync(settings);
            _logger.LogDebug("Saved setting: {Key} = {Value}", key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving setting {Key}", key);
            throw;
        }
    }

    private async Task<object?> LoadSettingInternalAsync(string key)
    {
        try
        {
            var settings = await LoadAllSettingsAsync();
            return settings.TryGetValue(key, out var value) ? value : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading setting {Key}", key);
            return null;
        }
    }

    private async Task<System.Collections.Generic.Dictionary<string, object>> LoadAllSettingsAsync()
    {
        if (!File.Exists(_settingsFile))
        {
            return new System.Collections.Generic.Dictionary<string, object>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_settingsFile);
            var settings = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, JsonElement>>(json);
            
            var result = new System.Collections.Generic.Dictionary<string, object>();
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
            return new System.Collections.Generic.Dictionary<string, object>();
        }
    }

    private async Task SaveAllSettingsAsync(System.Collections.Generic.Dictionary<string, object> settings)
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