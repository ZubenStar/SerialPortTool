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
    /// Gets the build date/time from compile-time generated constant
    /// </summary>
    public static string BuildTime
    {
        get
        {
            try
            {
                // Use build time from generated BuildInfo class (UTC timestamp)
                if (DateTime.TryParse(BuildInfo.BuildTimeUtc, out var buildTimeUtc))
                {
                    // Convert UTC to local time for display
                    return buildTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            catch
            {
                // Fallback if BuildInfo is not available
            }
            
            // Final fallback: use current time
            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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