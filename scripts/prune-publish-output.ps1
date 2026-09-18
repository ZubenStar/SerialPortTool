# Prune a self-contained WinUI publish output down to what we actually ship.
#
# This script is the SINGLE SOURCE for two rules that used to be duplicated between
# scripts/build-installer.ps1 and .github/workflows/release.yml:
#
#   1. debug symbols (*.pdb) are removed from the published output,
#   2. only the framework language resource folders listed in -KeepLanguageDirs are kept.
#
# Usage (mutates the directory in place):
#   .\scripts\prune-publish-output.ps1 -Path publish\x64
#
# Writes the removed language folder names to the success stream (one per line, nothing else), so a
# caller can capture them with `$removed = & .\scripts\prune-publish-output.ps1 -Path ...`.

param(
    [Parameter(Mandatory=$true)]
    [string]$Path,

    [string[]]$KeepLanguageDirs = @('zh-CN', 'en-us')
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Error "Publish directory not found: $Path"
    exit 1
}

# A language resource folder is named like a BCP-47 tag: `ll` or `ll-CC` / `ll-SC` (en-us, zh-CN,
# pt-BR, sr-Latn-RS). Case-sensitive, which keeps `Assets` / `runtimes` / `Microsoft.UI.Xaml` out.
$languageNamePattern = '^[a-z]{2,3}(?:-[A-Za-z0-9]+)*$'

# ...and it contains ONLY framework-localised resources. This second condition is what makes the rule
# safe to apply blindly: the real publish output of this project has 86 folder names matching the
# pattern (af-ZA, de-DE, ja-JP, …), 84 of them removable and each holding nothing but .mui files — but a
# future top-level folder called `lib`, `sdk` or `www` would match the name pattern too. Name alone is
# not enough to delete a directory from a build output; name plus "everything in here is a translated
# resource" is.
function Test-IsLocalizedResourceFolder {
    param([System.IO.DirectoryInfo]$Directory)

    $files = @(Get-ChildItem -LiteralPath $Directory.FullName -Recurse -File)
    if ($files.Count -eq 0) {
        return $false
    }

    foreach ($file in $files) {
        if ($file.Extension -ne '.mui' -and $file.Name -notlike '*.resources.dll') {
            return $false
        }
    }

    return $true
}

# Explicit foreach + -LiteralPath rather than `Get-ChildItem | Remove-Item`: piping FileInfo into
# Remove-Item fails parameter binding outright in this environment ("input object cannot be bound to
# any parameter"), which would abort the whole prune under $ErrorActionPreference = "Stop".
foreach ($pdb in @(Get-ChildItem -LiteralPath $Path -Recurse -Filter '*.pdb' -File)) {
    Remove-Item -LiteralPath $pdb.FullName -Force
}

$languageDirs = @(Get-ChildItem -LiteralPath $Path -Directory | Where-Object {
    $_.Name -match $languageNamePattern -and
    $_.Name -notin $KeepLanguageDirs -and
    (Test-IsLocalizedResourceFolder -Directory $_)
})

$removedNames = @($languageDirs | ForEach-Object { $_.Name })
foreach ($dir in $languageDirs) {
    Remove-Item -LiteralPath $dir.FullName -Recurse -Force
}

Write-Host ("Pruned {0}: removed {1} language resource folder(s), kept {2}; debug symbols removed" -f `
    $Path, $removedNames.Count, ($KeepLanguageDirs -join ', '))

# stdout contract: the removed names, nothing else.
$removedNames
