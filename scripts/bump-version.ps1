# Version Bump Script for SerialPortTool
# Usage: .\scripts\bump-version.ps1 -BumpType [major|minor|patch] [-Message "Optional commit message"]

param(
    [Parameter(Mandatory=$true)]
    [ValidateSet('major', 'minor', 'patch')]
    [string]$BumpType,
    
    [Parameter(Mandatory=$false)]
    [string]$Message = "",
    
    [Parameter(Mandatory=$false)]
    [switch]$NoCommit = $false
)

$ErrorActionPreference = "Stop"

# Get script directory and project root
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

# File paths
$versionJsonPath = Join-Path $projectRoot "version.json"
$csprojPath = Join-Path $projectRoot "SerialPortTool.csproj"

Write-Host "=== SerialPortTool Version Bump ===" -ForegroundColor Cyan
Write-Host "Bump Type: $BumpType" -ForegroundColor Yellow

# Read version.json
if (-not (Test-Path $versionJsonPath)) {
    Write-Error "version.json not found at $versionJsonPath"
    exit 1
}

$versionJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
$currentVersion = $versionJson.version

Write-Host "Current Version: $currentVersion" -ForegroundColor Gray

# Parse current version
if ($currentVersion -match '^(\d+)\.(\d+)\.(\d+)$') {
    $major = [int]$matches[1]
    $minor = [int]$matches[2]
    $patch = [int]$matches[3]
} else {
    Write-Error "Invalid version format in version.json: $currentVersion"
    exit 1
}

# Calculate new version
switch ($BumpType) {
    'major' {
        $major++
        $minor = 0
        $patch = 0
    }
    'minor' {
        $minor++
        $patch = 0
    }
    'patch' {
        $patch++
    }
}

$newVersion = "$major.$minor.$patch"
Write-Host "New Version: $newVersion" -ForegroundColor Green

# Update version.json
$versionJson.version = $newVersion

# Add new changelog entry
$today = Get-Date -Format "yyyy-MM-dd"
$changeMessage = if ($Message) { $Message } else { "Version $newVersion release" }

$newChangelogEntry = @{
    version = $newVersion
    date = $today
    changes = @($changeMessage)
}

# Insert at the beginning of changelog array
$newChangelog = @($newChangelogEntry) + $versionJson.changelog
$versionJson.changelog = $newChangelog

# Save version.json
$versionJson | ConvertTo-Json -Depth 10 | Set-Content $versionJsonPath -Encoding UTF8
Write-Host "✓ Updated version.json" -ForegroundColor Green

# Update .csproj file
if (-not (Test-Path $csprojPath)) {
    Write-Error "SerialPortTool.csproj not found at $csprojPath"
    exit 1
}

$csprojContent = Get-Content $csprojPath -Raw

# Update version numbers in .csproj
$csprojContent = $csprojContent -replace '<Version>[\d\.]+</Version>', "<Version>$newVersion</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d\.]+</AssemblyVersion>', "<AssemblyVersion>$newVersion.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d\.]+</FileVersion>', "<FileVersion>$newVersion.0</FileVersion>"

$csprojContent | Set-Content $csprojPath -Encoding UTF8
Write-Host "✓ Updated SerialPortTool.csproj" -ForegroundColor Green

# Git operations
if (-not $NoCommit) {
    Write-Host "`nGit Operations:" -ForegroundColor Cyan
    
    # Check if we're in a git repository
    $isGitRepo = Test-Path (Join-Path $projectRoot ".git")
    if (-not $isGitRepo) {
        Write-Warning "Not in a git repository. Skipping git operations."
        exit 0
    }
    
    # Stage the changed files
    Push-Location $projectRoot
    try {
        git add version.json SerialPortTool.csproj
        Write-Host "✓ Staged version files" -ForegroundColor Green
        
        # Commit the changes
        $commitMessage = "Bump version to $newVersion"
        if ($Message) {
            $commitMessage = "$commitMessage`n`n$Message"
        }
        git commit -m $commitMessage
        Write-Host "✓ Committed changes" -ForegroundColor Green
        
        # Create and push tag
        $tagName = "v$newVersion"
        git tag -a $tagName -m "Release $newVersion"
        Write-Host "✓ Created tag: $tagName" -ForegroundColor Green
        
        Write-Host "`nNext Steps:" -ForegroundColor Yellow
        Write-Host "1. Review the changes with: git show HEAD" -ForegroundColor White
        Write-Host "2. Push the commit: git push" -ForegroundColor White
        Write-Host "3. Push the tag to trigger release: git push origin $tagName" -ForegroundColor White
        Write-Host "`nThe GitHub Actions workflow will automatically build and create a release." -ForegroundColor Cyan
        
    } catch {
        Write-Error "Git operation failed: $_"
        Pop-Location
        exit 1
    }
    Pop-Location
} else {
    Write-Host "`nSkipped git operations (--NoCommit flag used)" -ForegroundColor Yellow
    Write-Host "`nManual Steps Required:" -ForegroundColor Yellow
    Write-Host "1. Review the changes in version.json and SerialPortTool.csproj" -ForegroundColor White
    Write-Host "2. Commit: git add version.json SerialPortTool.csproj && git commit -m 'Bump version to $newVersion'" -ForegroundColor White
    Write-Host "3. Tag: git tag -a v$newVersion -m 'Release $newVersion'" -ForegroundColor White
    Write-Host "4. Push: git push && git push origin v$newVersion" -ForegroundColor White
}

Write-Host "`n=== Version Bump Complete ===" -ForegroundColor Cyan