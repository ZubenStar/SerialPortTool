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
    /// Gets the build date/time from assembly
    /// This uses a compile-time constant that will be set during build
    /// </summary>
    public static string BuildTime
    {
        get
        {
            // Get build time from assembly informational version attribute
            var assembly = Assembly.GetExecutingAssembly();
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            
            if (attribute != null && attribute.InformationalVersion.Contains("+"))
            {
                // Parse build time from informational version (format: version+buildtime)
                var parts = attribute.InformationalVersion.Split('+');
                if (parts.Length > 1 && DateTime.TryParse(parts[1], out var buildTime))
                {
                    return buildTime.ToString("yyyy-MM-dd HH:mm:ss");
                }
            }
            
            // Fallback: use assembly build time from PE header
            return GetBuildDateTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    /// <summary>
    /// Gets the complete version info string
    /// </summary>
    public static string VersionString => $"v{Version} (Build: {BuildTime})";

    /// <summary>
    /// Gets the build date/time from the PE header
    /// </summary>
    private static DateTime GetBuildDateTime()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var filePath = assembly.Location;
            
            if (string.IsNullOrEmpty(filePath))
            {
                return DateTime.Now;
            }

            // Read PE header to get linker timestamp
            const int peHeaderOffset = 60;
            const int linkerTimestampOffset = 8;
            
            var buffer = new byte[2048];
            using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            {
                _ = stream.Read(buffer, 0, buffer.Length);
            }
            
            var secondsSince1970 = BitConverter.ToInt32(buffer, BitConverter.ToInt32(buffer, peHeaderOffset) + linkerTimestampOffset);
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var linkTimeUtc = epoch.AddSeconds(secondsSince1970);
            
            return linkTimeUtc.ToLocalTime();
        }
        catch
        {
            // If we can't read the PE header, return current time
            return DateTime.Now;
        }
    }
}