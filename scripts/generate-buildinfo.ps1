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

# BOM-less UTF-8 on purpose. [System.Text.Encoding]::UTF8 emits a BOM, and the scripts here were split
# between BOM / no-BOM writers — which turns every regenerated file into a whole-file diff and is the
# kind of noise that hides a real change. The generated file is ASCII-only, so no BOM is needed.
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutputPath, $content, $utf8NoBom)
Write-Host "Generated BuildInfo.g.cs with timestamp: $timestamp"