using SerialPortTool.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SerialPortTool.Tests;

/// <summary>
/// In-memory <see cref="ISettingsService"/>.
/// </summary>
/// <remarks>
/// Hand-written rather than mocked. The project has no mocking library and does not want one for a
/// seven-member interface, and a fake that actually stores values is more useful here than a mock that
/// only records calls: the services under test read back exactly what they wrote, which is what makes a
/// round-trip test mean something.
/// </remarks>
internal sealed class FakeSettingsService : ISettingsService
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public event EventHandler? SettingsLoadFailed;

    public bool HasLoadFailure { get; private set; }

    /// <summary>Seeds a value the way a previous session would have left it.</summary>
    public void Seed(string key, string value) => _values[key] = value;

    /// <summary>True when the key exists — the assertion that a save actually persisted.</summary>
    public bool Has(string key) => _values.ContainsKey(key);

    /// <summary>The raw stored string, or null.</summary>
    public string? Peek(string key) => _values.TryGetValue(key, out var value) ? value : null;

    /// <summary>Puts the service into the read-only protection state, as a corrupt file would.</summary>
    public void SimulateLoadFailure()
    {
        HasLoadFailure = true;
        SettingsLoadFailed?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveSettingAsync(string key, int value)
    {
        _values[key] = value.ToString();
        return Task.CompletedTask;
    }

    public Task<int> LoadSettingAsync(string key, int defaultValue)
        => Task.FromResult(
            _values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue);

    public Task SaveSettingAsync(string key, string value)
    {
        _values[key] = value;
        return Task.CompletedTask;
    }

    public Task<string> LoadSettingAsync(string key, string defaultValue)
        => Task.FromResult(_values.TryGetValue(key, out var value) ? value : defaultValue);

    public Task DeleteSettingAsync(string key)
    {
        _values.Remove(key);
        return Task.CompletedTask;
    }

    public Task ClearAsync()
    {
        _values.Clear();
        return Task.CompletedTask;
    }

    public Task FlushAsync() => Task.CompletedTask;
}
