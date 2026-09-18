namespace SerialPortTool.Core.Enums;

/// <summary>
/// User-selectable application appearance. Persisted to settings.json under the key
/// <c>AppTheme</c> as the enum name, so the stored value stays readable and reorder-safe.
/// </summary>
public enum AppThemePreference
{
    /// <summary>Follow the Windows app theme (default).</summary>
    System = 0,

    /// <summary>Force the light "daylight bench" palette.</summary>
    Light = 1,

    /// <summary>Force the dark "bench graphite" palette.</summary>
    Dark = 2
}
