using Microsoft.Extensions.Logging;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// JSON-backed named port configuration presets, stored in one <c>settings.json</c> string key.
/// </summary>
/// <remarks>
/// The validation here is a second line of defence, not the primary one: <see cref="SerialPortProfile"/>
/// values are also sanitised on read by <see cref="SerialPortConfigCodec.Sanitize"/>. Worth having both
/// because they answer different questions — this one says "the name is usable before it is saved", that
/// one says "a hand-edited settings.json cannot produce parameters SerialPort refuses".
/// </remarks>
public sealed class PortPresetService : IPortPresetService
{
    /// <summary>Settings key holding the serialized preset library.</summary>
    public const string LibrarySettingKey = "PortPresets";

    /// <summary>
    /// 预设数量上限。
    /// </summary>
    /// <remarks>
    /// A bound rather than a policy: every preset is serialised into one settings value that is rewritten
    /// whole on each save, so an unbounded list is a setting that grows without limit. Twelve is more than
    /// a bench with six adapters needs.
    /// </remarks>
    public const int MaxPresets = 12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<PortPresetService> _logger;

    public PortPresetService(ISettingsService settings, ILogger<PortPresetService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PortPreset>> LoadAsync()
    {
        string raw;
        try
        {
            raw = await _settings.LoadSettingAsync(LibrarySettingKey, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the port preset library");
            return Array.Empty<PortPreset>();
        }

        if (!SerialPortConfigCodec.TryParseList<PortPreset>(raw, JsonOptions, out var parsed))
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("The port preset library could not be parsed; starting empty");
            }

            return Array.Empty<PortPreset>();
        }

        return Normalize(parsed);
    }

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<PortPreset> presets)
    {
        var list = new List<PortPreset>(presets.Count);

        foreach (var preset in presets)
        {
            list.Add(new PortPreset
            {
                Id = preset.Id,
                Name = Trim(preset.Name, PortPreset.MaxNameLength),
                Profile = SerialPortConfigCodec.Sanitize(preset.Profile),
            });
        }

        return _settings.SaveSettingAsync(LibrarySettingKey, JsonSerializer.Serialize(list, JsonOptions));
    }

    /// <inheritdoc />
    public string? Validate(PortPreset preset, IReadOnlyList<PortPreset> existing, string? currentId = null)
    {
        var name = Trim(preset.Name, PortPreset.MaxNameLength);

        if (string.IsNullOrWhiteSpace(name))
        {
            return "请先给这套档案起个名字";
        }

        // Uniqueness is case-insensitive because these names are read from a menu, where two rows differing
        // only in case are indistinguishable — and picking the wrong one silently applies the wrong setup.
        foreach (var candidate in existing)
        {
            if (string.Equals(candidate.Id, currentId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return $"已存在名为“{name}”的档案，请换一个名字";
            }
        }

        if (existing.Count >= MaxPresets && string.IsNullOrEmpty(currentId))
        {
            return $"最多只能保存 {MaxPresets} 套档案，请先删除不用的";
        }

        return null;
    }

    /// <inheritdoc />
    public PortPreset Create(SerialPortProfile profile, string name) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = Trim(name, PortPreset.MaxNameLength),
        Profile = SerialPortConfigCodec.Sanitize(profile),
    };

    private static IReadOnlyList<PortPreset> Normalize(IEnumerable<PortPreset> presets)
    {
        var result = new List<PortPreset>();

        foreach (var preset in presets)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Name))
            {
                // An unnamed preset has nothing to identify it by in the menu, so it is not one.
                continue;
            }

            if (string.IsNullOrWhiteSpace(preset.Id))
            {
                preset.Id = Guid.NewGuid().ToString("N");
            }

            preset.Name = Trim(preset.Name, PortPreset.MaxNameLength);
            preset.Profile = SerialPortConfigCodec.Sanitize(preset.Profile);

            result.Add(preset);

            if (result.Count >= MaxPresets)
            {
                break;
            }
        }

        return result;
    }

    private static string Trim(string? value, int maxLength)
    {
        var flattened = string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value!.Replace('\r', ' ').Replace('\n', ' ').Trim();

        return flattened.Length <= maxLength ? flattened : flattened[..maxLength];
    }
}
