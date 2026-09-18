using System;
using System.Reflection;

namespace SerialPortTool.Helpers;

/// <summary>
/// Provides version and build information for the application
/// </summary>
public static class VersionInfo
{
    /// <summary>
    /// Gets the application version from assembly
    /// </summary>
    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
        }
    }

    /// <summary>
    /// Gets the full version string including revision
    /// </summary>
    public static string FullVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version != null ? version.ToString() : "1.0.0.0";
        }
    }

    /// <summary>
    /// Gets the build date/time from compile-time generated constant.
    /// </summary>
    /// <remarks>
    /// Falls back to the raw <see cref="BuildInfo.BuildTimeUtc"/> string, never to <c>DateTime.Now</c>.
    /// Returning the current time meant the About dialog showed "构建时间: " + right-now whenever the
    /// generated constant was missing or unparseable — indistinguishable from a correct value, and it
    /// made "which build am I running?" unanswerable in exactly the situation where that matters.
    /// </remarks>
    public static string BuildTime
    {
        get
        {
            var raw = BuildInfo.BuildTimeUtc;

            if (DateTime.TryParse(
                    raw,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var buildTimeUtc))
            {
                // Convert UTC to local time for display
                return buildTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            }

            return string.IsNullOrWhiteSpace(raw) ? "unknown" : raw;
        }
    }

    /// <summary>
    /// Gets the build date/time in UTC
    /// </summary>
    public static string BuildTimeUtc => BuildInfo.BuildTimeUtc;

    /// <summary>
    /// Gets the complete version info string
    /// </summary>
    public static string VersionString => $"v{Version} (Build: {BuildTime})";

}