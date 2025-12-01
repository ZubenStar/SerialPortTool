# Generate Release Notes from version.json
# Usage: .\scripts\generate-release-notes.ps1 [-Version "1.0.0"] [-OutputFile "release-notes.md"]

param(
    [Parameter(Mandatory=$false)]
    [string]$Version = "",
    
    [Parameter(Mandatory=$false)]
    [string]$OutputFile = "release-notes.md"
)

$ErrorActionPreference = "Stop"

# Get script directory and project root
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

# File paths
$versionJsonPath = Join-Path $projectRoot "version.json"

Write-Host "=== Generating Release Notes ===" -ForegroundColor Cyan

# Read version.json
if (-not (Test-Path $versionJsonPath)) {
    Write-Error "version.json not found at $versionJsonPath"
    exit 1
}

$versionJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json

# Determine which version to use
if (-not $Version) {
    $Version = $versionJson.version
    Write-Host "Using current version: $Version" -ForegroundColor Yellow
} else {
    Write-Host "Using specified version: $Version" -ForegroundColor Yellow
}

# Find the changelog entry for the specified version
$changelog = $versionJson.changelog | Where-Object { $_.version -eq $Version } | Select-Object -First 1

if ($null -eq $changelog) {
    Write-Error "No changelog found for version $Version in version.json"
    Write-Host "Available versions:" -ForegroundColor Yellow
    $versionJson.changelog | ForEach-Object { Write-Host "  - $($_.version)" -ForegroundColor Gray }
    exit 1
}

Write-Host "Found changelog for version $Version" -ForegroundColor Green

# Generate release notes in Markdown format
$releaseNotes = @"
# SerialPortTool $Version

**Release Date:** $($changelog.date)

## What's Changed

$($changelog.changes | ForEach-Object { "- $_" } | Out-String)

## Downloads

Choose the appropriate package for your system:
- **x64**: For standard 64-bit Windows PCs
- **ARM64**: For ARM-based Windows devices (Surface Pro X, etc.)

Each package is self-contained and requires no additional dependencies.

## Installation

1. Download the appropriate ZIP file for your platform
2. Extract to a folder of your choice
3. Run ``SerialPortTool.exe``

## System Requirements

- Windows 10 version 1809 (build 17763) or later
- Windows 11 (recommended)

---

**Full Changelog**: https://github.com/YOUR_USERNAME/SerialPortTool/compare/v$Version...HEAD
"@

# Save to file
$outputPath = Join-Path $projectRoot $OutputFile
$releaseNotes | Out-File -FilePath $outputPath -Encoding utf8

Write-Host "`n✓ Release notes generated: $OutputFile" -ForegroundColor Green
Write-Host "`nPreview:" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Gray
Write-Host $releaseNotes
Write-Host "========================================" -ForegroundColor Gray

# Show statistics
$changeCount = $changelog.changes.Count
Write-Host "`nStatistics:" -ForegroundColor Cyan
Write-Host "  Version: $Version" -ForegroundColor White
Write-Host "  Date: $($changelog.date)" -ForegroundColor White
Write-Host "  Changes: $changeCount" -ForegroundColor White
Write-Host "  Output: $outputPath" -ForegroundColor White

Write-Host "`n=== Generation Complete ===" -ForegroundColor Cyan