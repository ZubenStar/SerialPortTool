using Microsoft.Extensions.Logging;
using SerialPortTool.Helpers;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// JSON-backed per-port notes / tags / group, stored in one <c>settings.json</c> string key.
/// </summary>
/// <remarks>
/// One key rather than one key per field: these values are always read together (the port list wants all
/// three for every row) and written together (the editor sets whichever changed, then persists the set).
/// Splitting them into <c>PortNotes_&lt;port&gt;</c> / <c>PortTags_&lt;port&gt;</c> / … would triple the
/// number of round-trips for no benefit, and would leave behind orphans when a port disappears.
/// </remarks>
public sealed class PortMetadataService : IPortMetadataService
{
    /// <summary>Settings key holding the serialized metadata set.</summary>
    public const string MetadataSettingKey = "PortMetadata";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<PortMetadataService> _logger;

    public PortMetadataService(ISettingsService settings, ILogger<PortMetadataService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, PortMetadata>> LoadAsync()
    {
        string raw;
        try
        {
            raw = await _settings.LoadSettingAsync(MetadataSettingKey, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the port metadata setting");
            return Empty.Instance;
        }

        if (!SerialPortConfigCodec.TryParseList<PortMetadata>(raw, JsonOptions, out var parsed))
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("The port metadata could not be parsed; starting without notes");
            }

            return Empty.Instance;
        }

        return Normalize(parsed);
    }

    /// <inheritdoc />
    public Task SaveAsync(IEnumerable<PortMetadata> metadata)
    {
        var list = new List<PortMetadata>();

        foreach (var entry in metadata)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.PortName) || entry.IsEmpty)
            {
                // A row the user emptied is not a row to keep — otherwise closing the editor leaves behind
                // a blank note forever, growing the setting one entry per port ever edited.
                continue;
            }

            list.Add(Sanitize(entry));
        }

        return _settings.SaveSettingAsync(MetadataSettingKey, JsonSerializer.Serialize(list, JsonOptions));
    }

    /// <inheritdoc />
    public PortMetadata Sanitize(PortMetadata metadata) => SanitizeCore(metadata);

    private static IReadOnlyDictionary<string, PortMetadata> Normalize(IEnumerable<PortMetadata> items)
    {
        var result = new Dictionary<string, PortMetadata>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.PortName))
            {
                continue;
            }

            // Case-insensitive keying: `COM3` and `com3` are the same port (established in v2.1.3), so a
            // note written against one spelling must be found by the other.
            result[item.PortName.Trim()] = SanitizeCore(item);
        }

        return result;
    }

    private static PortMetadata SanitizeCore(PortMetadata metadata) => new()
    {
        PortName = (metadata.PortName ?? string.Empty).Trim(),
        Notes = Clamp(Flatten(metadata.Notes), PortMetadata.MaxNotesLength),
        Tags = Clamp(Flatten(metadata.Tags), PortMetadata.MaxTagsLength),
        Group = Clamp(Flatten(metadata.Group), PortMetadata.MaxGroupLength),
    };

    /// <summary>
    /// 备注 / 标签 / 分组都是单行显示，所以内部的换行先折叠成空格，两端的空白去掉。
    /// </summary>
    private static string Flatten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // \r\n first, so a Windows line break collapses to one space and not two — the same normalisation
        // SendLineEndings applies to a payload, for the same reason: a doubled space mid-note is visible
        // in the row and looks like the user typed it.
        return value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string Clamp(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static class Empty
    {
        public static readonly IReadOnlyDictionary<string, PortMetadata> Instance =
            new Dictionary<string, PortMetadata>(StringComparer.OrdinalIgnoreCase);
    }
}
