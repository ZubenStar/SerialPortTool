using Microsoft.Extensions.Logging;
using SerialPortTool.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SerialPortTool.Services;

/// <summary>
/// JSON-backed quick-send library on top of <see cref="ISettingsService"/>.
/// </summary>
/// <remarks>
/// The library is one settings value rather than a file of its own: it then inherits the settings
/// service's atomic write (temp file + replace) and its read-only protection state for free, which is
/// exactly the durability a small user-authored list needs. A separate file would have to
/// re-implement both.
/// </remarks>
public sealed class SnippetService : ISnippetService
{
    /// <summary>Settings key holding the serialized library.</summary>
    /// <remarks>
    /// Not a documented settings key elsewhere: it only ever passes through
    /// <see cref="ISettingsService"/>'s string overloads, so it needs no entry in the settings table
    /// and no migration story of its own.
    /// </remarks>
    public const string LibrarySettingKey = "QuickSendSnippets";

    /// <summary>
    /// Longest label derived from a payload when the user did not supply one.
    /// </summary>
    private const int DerivedLabelMaxLength = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // The value is hand-inspectable in settings.json, so a stray property from a newer build (or
        // a hand edit) must be ignored rather than rejecting the whole library.
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private readonly ISettingsService _settings;
    private readonly ILogger<SnippetService> _logger;

    public SnippetService(ISettingsService settings, ILogger<SnippetService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SendSnippet>> LoadAsync()
    {
        string raw;
        try
        {
            raw = await _settings.LoadSettingAsync(LibrarySettingKey, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the quick-send library setting");
            return Array.Empty<SendSnippet>();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<SendSnippet>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<SendSnippet>>(raw, JsonOptions);
            return parsed is null ? Array.Empty<SendSnippet>() : Normalize(parsed);
        }
        catch (JsonException ex)
        {
            // A corrupt value costs the user their library, so say so in the log rather than
            // swallowing it. Returning empty is still right: the feature is a convenience, and the
            // alternative is refusing to start over a convenience feature's payload.
            _logger.LogWarning(ex, "The quick-send library could not be parsed; starting with an empty one");
            return Array.Empty<SendSnippet>();
        }
    }

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<SendSnippet> snippets)
    {
        var payload = JsonSerializer.Serialize(snippets, JsonOptions);
        return _settings.SaveSettingAsync(LibrarySettingKey, payload);
    }

    /// <inheritdoc />
    public string ExpandVariables(string content, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content ?? string.Empty;
        }

        var local = now.LocalDateTime;
        var builder = new StringBuilder(content.Length + 16);

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (c == '\\' && i + 1 < content.Length)
            {
                switch (content[i + 1])
                {
                    case 'r': builder.Append('\r'); i++; continue;
                    case 'n': builder.Append('\n'); i++; continue;
                    case 't': builder.Append('\t'); i++; continue;
                    case '0': builder.Append('\0'); i++; continue;
                    case '\\': builder.Append('\\'); i++; continue;
                    default:
                        // Not an escape we know. Fall through and emit the backslash literally.
                        break;
                }
            }

            if (c == '$' && i + 1 < content.Length && content[i + 1] == '{')
            {
                var close = content.IndexOf('}', i + 2);
                if (close > 0 &&
                    TryResolveToken(content.AsSpan(i + 2, close - i - 2), local, out var value))
                {
                    builder.Append(value);
                    i = close;
                    continue;
                }

                // Unknown or unterminated token: emitted verbatim by the append below.
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolves one <c>${...}</c> token. A span parameter so the caller does not allocate a substring
    /// per token.
    /// </summary>
    private static bool TryResolveToken(
        ReadOnlySpan<char> name,
        DateTime localNow,
        out string value)
    {
        name = name.Trim();

        if (name.Equals("date", StringComparison.OrdinalIgnoreCase))
        {
            value = localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        if (name.Equals("time", StringComparison.OrdinalIgnoreCase))
        {
            value = localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return true;
        }

        if (name.Equals("datetime", StringComparison.OrdinalIgnoreCase))
        {
            value = localNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return true;
        }

        if (name.Equals("epoch", StringComparison.OrdinalIgnoreCase))
        {
            value = new DateTimeOffset(localNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (name.Equals("epochms", StringComparison.OrdinalIgnoreCase))
        {
            value = new DateTimeOffset(localNow).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Drops unusable entries and repairs the identity/label of the rest, so callers never have to
    /// null-check what they loaded.
    /// </summary>
    private static IReadOnlyList<SendSnippet> Normalize(IEnumerable<SendSnippet> snippets)
    {
        var result = new List<SendSnippet>();

        foreach (var snippet in snippets)
        {
            if (snippet is null || string.IsNullOrEmpty(snippet.Content))
            {
                // An entry with no payload cannot be sent, so it is not a snippet. Dropping it here
                // keeps the flyout free of dead rows that do nothing when clicked.
                continue;
            }

            if (string.IsNullOrWhiteSpace(snippet.Id))
            {
                snippet.Id = Guid.NewGuid().ToString("N");
            }

            snippet.Group ??= string.Empty;

            if (string.IsNullOrWhiteSpace(snippet.Label))
            {
                snippet.Label = DeriveLabel(snippet.Content);
            }

            result.Add(snippet);
        }

        return result;
    }

    /// <summary>Builds a readable label from a payload that arrived without one.</summary>
    private static string DeriveLabel(string content)
    {
        // Collapse line breaks first: a label is one line in the menu, and a multi-line payload
        // truncated mid-way reads as garbage.
        var flattened = content.Replace('\r', ' ').Replace('\n', ' ').Trim();

        if (flattened.Length == 0)
        {
            return "(未命名)";
        }

        return flattened.Length <= DerivedLabelMaxLength
            ? flattened
            : flattened[..DerivedLabelMaxLength] + "…";
    }
}
