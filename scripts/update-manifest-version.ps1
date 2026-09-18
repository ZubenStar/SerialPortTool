param(
    [Parameter(Mandatory=$true)]
    [string]$ManifestPath,

    [Parameter(Mandatory=$true)]
    [string]$Version
)

# Fail loudly. Without this, a missing file or an unreadable path left Get-Content returning $null and
# the script carried on to a no-op "$null -replace ..." — then exit 0, which the MSBuild `Exec` task
# reports as success, so Package.appxmanifest silently kept the previous release's version forever.
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
    Write-Error "Package manifest not found: $ManifestPath"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "Version argument is empty; refusing to touch $ManifestPath"
    exit 1
}

# Read the manifest content
$content = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8

# Update only the Identity Version attribute using a more precise pattern
$pattern = '(<Identity\s[^>]*\bVersion=")([^"]+)(")'

# A missing Identity element means the manifest was damaged or replaced. Reporting success here is
# exactly how the version drifts unnoticed, so this is an error, not a warning.
if ($content -notmatch $pattern) {
    Write-Error "No '<Identity ... Version=""..."">' element found in $ManifestPath; the manifest cannot be versioned."
    exit 1
}

# IMPORTANT: group references must be written as ${1}/${2}, never $1/$2.
# "$1" immediately followed by a version that starts with a digit (e.g. 1.7.0.0)
# is parsed by .NET as the (non-existent) group "$11", which silently wrote a
# literal "$11.7.0.0" into the manifest and destroyed the Identity element.
$updated = [regex]::Replace($content, $pattern, "`${1}$Version`${3}", 1)

# Post-condition, not a change check: after the first build the manifest is already correct and the
# replace is a no-op, which is the normal steady state — what must never happen is a *write* that leaves
# a different version behind.
$verify = [regex]::Match($updated, $pattern)
if (-not $verify.Success -or $verify.Groups[2].Value -ne $Version) {
    $found = if ($verify.Success) { $verify.Groups[2].Value } else { "<none>" }
    Write-Error "Post-condition failed for $ManifestPath : Identity Version is '$found' but '$Version' was expected."
    exit 1
}

# Write back with UTF-8 BOM encoding (the MSIX tooling expects the BOM here; do not "normalise" this
# to the BOM-less writer the other scripts use).
$utf8Bom = New-Object System.Text.UTF8Encoding $true
[System.IO.File]::WriteAllText($ManifestPath, $updated, $utf8Bom)

Write-Host "Updated manifest Identity version to $Version"
