<#
.SYNOPSIS
    Fails when the three theme dictionaries in Themes/Tokens.xaml do not define the same set of keys.

.DESCRIPTION
    A key that exists in Light and Dark but not in HighContrast is a RUNTIME crash. The XAML compiler does
    not resolve ThemeResource keys, so the build is perfectly happy and the window throws only when the
    system is running high contrast. That is the worst shape a bug can have here: it ships, it passes
    every build, and it fires for the subset of users least able to work around it.

    That is why this is a build step (see the VerifyThemeParity target in SerialPortTool.csproj) and not a
    convention people are asked to remember.

    Deliberately strict about its own preconditions: if the three <ResourceDictionary x:Key="..."> blocks
    cannot be located, it FAILS rather than passing quietly. A checker that has stopped checking is worse
    than no checker, and the alternative (a silent pass after someone refactors Tokens.xaml) would leave
    the guard in place while it protects nothing.

    TWO WINDOWS POWERSHELL TRAPS THIS FILE WORKS AROUND. Both were hit while writing it, and both produce
    failures that look like something else:

      1. This file must stay pure ASCII. Windows PowerShell 5.1 decodes a .ps1 that has no UTF-8 BOM
         using the ANSI code page, so a non-ASCII character in a message here comes out as mojibake in
         the build log (an em dash rendered as "\u9225?"). ASCII is not a style preference in this file.
      2. $TokensPath deliberately does NOT default to a $PSScriptRoot expression. In a param() block
         that variable can still be empty under -File, which made the no-argument invocation die in
         Join-Path before doing anything. It is resolved in the body instead, where it is populated.

.PARAMETER TokensPath
    The token dictionary to verify. Defaults to Themes/Tokens.xaml next to this script.

.PARAMETER Themes
    The theme names to compare, in order. The first one is the baseline.

.OUTPUTS
    Exit code 0 when every theme defines the same keys; 1 on mismatch or when the structure cannot be found.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\verify-theme-parity.ps1
#>
[CmdletBinding()]
param(
    [string]$TokensPath = '',
    [string[]]$Themes = @('Light', 'Dark', 'HighContrast')
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than in the param block -- see trap 2 above.
if ([string]::IsNullOrWhiteSpace($TokensPath)) {
    $TokensPath = Join-Path $PSScriptRoot '..\Themes\Tokens.xaml'
}

if (-not (Test-Path -LiteralPath $TokensPath)) {
    Write-Host "theme-parity: cannot read '$TokensPath'."
    exit 1
}

# Explicit encoding, always: this dictionary is UTF-8 with Chinese and typographic characters in it, and
# Get-Content's default decoding on Windows is not UTF-8. (It also trips the repo's shell guard, for the
# same reason.)
$text = Get-Content -Raw -Encoding UTF8 -LiteralPath $TokensPath

$keys = @{}
foreach ($theme in $Themes) {
    # Each block runs from its own header up to the NEXT theme header, or to the end of the
    # ThemeDictionaries element. Matching with a lookahead rather than counting </ResourceDictionary>
    # lines keeps this immune to a nested dictionary being added inside a theme later.
    $pattern = '<ResourceDictionary x:Key="' + [regex]::Escape($theme) + '">([\s\S]*?)(?=<ResourceDictionary x:Key="|</ResourceDictionary\.ThemeDictionaries>)'
    $match = [regex]::Match($text, $pattern)

    if (-not $match.Success) {
        Write-Host "theme-parity: could not locate the '$theme' dictionary in '$TokensPath'."
        Write-Host "  The check cannot run, so it must not pass. If Tokens.xaml was restructured, update"
        Write-Host "  this script's block pattern - do not leave the guard silently disabled."
        exit 1
    }

    # @() so a single-key theme stays an array instead of unrolling to a scalar.
    $keys[$theme] = @(
        [regex]::Matches($match.Groups[1].Value, 'x:Key="([^"]+)"') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object -Unique
    )
}

$baseline = $Themes[0]
$mismatched = $false

foreach ($theme in $Themes | Select-Object -Skip 1) {
    $missingFromTheme = @($keys[$baseline] | Where-Object { $keys[$theme] -notcontains $_ })
    $missingFromBase = @($keys[$theme] | Where-Object { $keys[$baseline] -notcontains $_ })

    if ($missingFromTheme.Count -eq 0 -and $missingFromBase.Count -eq 0) {
        continue
    }

    $mismatched = $true
    Write-Host "theme-parity: '$baseline' and '$theme' do not define the same keys."

    foreach ($key in $missingFromTheme) {
        Write-Host "  only in ${baseline}, MISSING from ${theme}: $key"
    }

    foreach ($key in $missingFromBase) {
        Write-Host "  only in ${theme}, MISSING from ${baseline}: $key"
    }
}

if ($mismatched) {
    Write-Host ""
    Write-Host "Every ThemeResource key must exist in all three dictionaries. A missing key is not a styling"
    Write-Host "problem - it throws at runtime as soon as the affected element is created in that theme, and"
    Write-Host "only in that theme. Add the key to the missing dictionary, or remove it from all three."
    exit 1
}

Write-Host "theme-parity: $($Themes -join ' / ') each define $($keys[$baseline].Count) keys."
exit 0
