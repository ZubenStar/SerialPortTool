param(
    [string]$OutputPath
)

$timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')

$content = @"
// This file is auto-generated during build
// DO NOT EDIT MANUALLY

using System;

namespace SerialPortTool.Helpers;

internal static class BuildInfo
{
    public const string BuildTimeUtc = "$timestamp";
}
"@

[System.IO.File]::WriteAllText($OutputPath, $content, [System.Text.Encoding]::UTF8)
Write-Host "Generated BuildInfo.g.cs with timestamp: $timestamp"