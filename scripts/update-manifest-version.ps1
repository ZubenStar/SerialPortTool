param(
    [Parameter(Mandatory=$true)]
    [string]$ManifestPath,

    [Parameter(Mandatory=$true)]
    [string]$Version
)

# Read the manifest content
$content = Get-Content $ManifestPath -Raw -Encoding UTF8

# Update only the Identity Version attribute using a more precise pattern
$pattern = '(<Identity\s[^>]*\bVersion=")[^"]+(")'

if ($content -notmatch $pattern) {
    Write-Warning "No '<Identity ... Version=\"...\">' element found in $ManifestPath - manifest left unchanged."
    exit 0
}

# IMPORTANT: group references must be written as ${1}/${2}, never $1/$2.
# "$1" immediately followed by a version that starts with a digit (e.g. 1.7.0.0)
# is parsed by .NET as the (non-existent) group "$11", which silently wrote a
# literal "$11.7.0.0" into the manifest and destroyed the Identity element.
$replacement = "`${1}$Version`${2}"
$content = $content -replace $pattern, $replacement

# Write back with UTF-8 BOM encoding
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText($ManifestPath, $content, $utf8Bom)

Write-Host "Updated manifest Identity version to $Version"
