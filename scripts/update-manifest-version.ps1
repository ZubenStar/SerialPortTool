param(
    [Parameter(Mandatory=$true)]
    [string]$ManifestPath,

    [Parameter(Mandatory=$true)]
    [string]$Version
)

# Read the manifest content
$content = Get-Content $ManifestPath -Raw -Encoding UTF8

# Update only the Identity Version attribute (not TargetDeviceFamily MinVersion)
$pattern = '(<Identity[^>]*Version=")[^"]+(")'
$replacement = "`$1$Version`$2"
$content = $content -replace $pattern, $replacement

# Write back with UTF-8 BOM encoding
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText($ManifestPath, $content, $utf8Bom)

Write-Host "Updated manifest Identity version to $Version"
