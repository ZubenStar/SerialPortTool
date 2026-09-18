# Build Installer Script for SerialPortTool
# Usage: .\scripts\build-installer.ps1 [-Configuration Release] [-SkipPublish]
#
# 本地与 CI 共用同一套 publish 参数，避免参数漂移导致安装包与便携 ZIP 内容不一致。
# 依赖：.NET 9 SDK + Inno Setup 6（ISCC.exe）。

param(
    [Parameter(Mandatory=$false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory=$false)]
    [switch]$SkipPublish = $false
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

$versionJsonPath = Join-Path $projectRoot "version.json"
$issPath = Join-Path $projectRoot "installer\SerialPortTool.iss"
$csprojPath = Join-Path $projectRoot "SerialPortTool.csproj"
$publishDir = Join-Path $projectRoot "publish\x64"
$outputDir = Join-Path $projectRoot "packages\installer"

Write-Host "=== SerialPortTool Installer Build ===" -ForegroundColor Cyan

# ---- 1. 版本号（唯一来源：version.json） ----
if (-not (Test-Path $versionJsonPath)) {
    Write-Error "version.json not found at $versionJsonPath"
    exit 1
}

$version = (Get-Content $versionJsonPath -Raw | ConvertFrom-Json).version.Trim()

# The .iss has no fallback for #AppVersion on purpose (a 0.0.0 install package permanently breaks the
# auto-update path on every machine that installs it), so validate it here where the message is useful.
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    Write-Error "version.json 'version' must be three numeric parts (e.g. 2.1.0); got '$version'"
    exit 1
}

Write-Host "Version: $version" -ForegroundColor Gray

# ---- 2. 自包含发布（参数与 .github/workflows/release.yml 保持一致） ----
if (-not $SkipPublish) {
    Write-Host "`nPublishing self-contained x64 build..." -ForegroundColor Yellow

    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }

    & dotnet publish $csprojPath `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDir `
        -p:PublishTrimmed=false `
        -p:PublishReadyToRun=false `
        -p:PublishSingleFile=false

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
} else {
    Write-Host "`nSkipped dotnet publish (-SkipPublish)" -ForegroundColor Yellow
}

$publishedExe = Join-Path $publishDir "SerialPortTool.exe"
if (-not (Test-Path $publishedExe)) {
    Write-Error "Publish output not found: $publishedExe"
    exit 1
}

# ---- 3. 定位 Inno Setup 编译器 ----
# Inno Setup 的「仅为我安装」会把 ISCC.exe 放到用户目录下，系统级安装才在 Program Files，
# 两种都要探测，否则本地（用户级安装）会误报找不到编译器。
$isccCandidates = @()
if ($env:LOCALAPPDATA) {
    $isccCandidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
}
if (${env:ProgramFiles(x86)}) {
    $isccCandidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
}
if ($env:ProgramFiles) {
    $isccCandidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
}

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $iscc = $command.Source
    }
}
if (-not $iscc) {
    Write-Error ("ISCC.exe (Inno Setup 6) not found. Install it from https://jrsoftware.org/isdl.php and re-run. " +
                 "Looked in: " + ($isccCandidates -join '; '))
    exit 1
}
Write-Host "`nUsing ISCC: $iscc" -ForegroundColor Gray

# ---- 4. 编译安装程序 ----
if (-not (Test-Path $issPath)) {
    Write-Error "Installer script not found: $issPath"
    exit 1
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# 框架语言资源过滤：规则（保留清单 + 目录名模式）集中在 scripts/prune-publish-output.ps1，
# 便携 ZIP 侧（release.yml）调用同一个脚本，不再各自维护一份拷贝。
# 自包含发布默认带 80+ 个语言目录，每个含 Microsoft.ui.xaml*.mui，对中英文用户毫无用处。
# 这里就地裁剪 publish 目录，安装包与便携 ZIP 因此拿到完全一致的树。
$pruneScript = Join-Path $scriptDir "prune-publish-output.ps1"
# The callee sets $ErrorActionPreference = "Stop" itself, so a missing directory aborts this script
# through the terminating error — no $LASTEXITCODE check is needed (and none is wanted: it would still
# hold a stale value on the -SkipPublish path, where no native command has run yet).
$removedLanguageDirs = @(& $pruneScript -Path $publishDir)

Write-Host ("Removed {0} framework language folder(s) from the publish output" -f
    $removedLanguageDirs.Count) -ForegroundColor Gray

# Pruning already deleted them, so Excludes only carries the debug-symbol guard for a direct ISCC run.
$excludeValue = '*.pdb'

$isccArgs = @(
    "/DAppVersion=$version"
    "/DSourceDir=$publishDir"
    "/DOutputDir=$outputDir"
    "/DExcludes=$excludeValue"
)

# Inno Setup 官方安装包不包含简体中文语言文件，存在时才启用中文安装界面。
$chineseIsl = Join-Path (Split-Path -Parent $iscc) "Languages\ChineseSimplified.isl"
if (Test-Path $chineseIsl) {
    $isccArgs = @("/DIncludeChinese=1") + $isccArgs
    Write-Host "Chinese (Simplified) installer language detected" -ForegroundColor Gray
}

$isccArgs += $issPath

Write-Host "`nCompiling installer..." -ForegroundColor Yellow
& $iscc @isccArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "ISCC failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$setupPath = Join-Path $outputDir "SerialPortTool-Setup-v$version-win-x64.exe"
if (-not (Test-Path $setupPath)) {
    Write-Error "Expected installer not found: $setupPath"
    exit 1
}

$sizeMb = [math]::Round((Get-Item $setupPath).Length / 1MB, 1)
Write-Host "`nInstaller created: $setupPath ($sizeMb MB)" -ForegroundColor Green
Write-Host "=== Installer Build Complete ===" -ForegroundColor Cyan
