<#
.SYNOPSIS
    Drives the running app through UI Automation to verify keyboard entry points that cannot be tested any
    other way.

.DESCRIPTION
    The only UI-level automated check in this repo. Everything else that touches XAML is covered by the
    manual checklist in AGENTS.md, because logic tests cannot see a window and a synthetic
    KeyRoutedEventArgs cannot honestly answer "does this key arrive". This script launches the real app,
    focuses the real search box, and sends real key input.

    It answers the question that motivated it: F2 must reach MainWindow.OnRootKeyDown both while focus is
    inside a TextBox and while a Flyout is open. A Flyout hosts its content in its own popup tree, so
    whether the window-root handler still sees the key was genuinely uncertain until this was run.

    Checks performed:
      1. the window appears and its UI thread is responsive
      2. a TextBox can be focused, and the palette opens on F2 (list controls +1) and closes on a second F2
      3. with a menu-bar Flyout open, F2 still opens the palette
      4. the app is still responding at the end

    Exits 0 when every check passes, 1 otherwise. Screenshots of each step are written next to the repo
    under obj\uia-smoke so a failure can be looked at rather than guessed at.

    LOCAL ONLY - DO NOT WIRE THIS INTO CI. It needs an interactive desktop, a foreground window and a
    real cursor focus; on a headless or locked agent it would fail for reasons that have nothing to do
    with the app.

    THREE TRAPS ARE WORKED AROUND HERE, ALL LEARNED THE HARD WAY IN THIS REPO. Read verify-theme-parity.ps1
    for the first two:

      * Pure ASCII. Windows PowerShell 5.1 decodes a .ps1 with no UTF-8 BOM as ANSI, so a non-ASCII
        character - even in a comment-eating message - becomes mojibake, and a non-ASCII literal used as a
        selector silently never matches. This script contains none, and targets the menu bar by INDEX.
      * Paths are resolved in the body, never in a param() default: $PSScriptRoot is not reliably
        populated there under -File.
      * Helpers return their collection with a leading comma. Without it PowerShell enumerates the
        AutomationElementCollection, and a ONE-element result collapses into a bare element - so the next
        .Item(0) throws "does not contain a method named 'Item'". The menu bar is exactly one element, so
        this bug hid until the second run.

.PARAMETER ExePath
    The app to drive. Defaults to the Release build next to this script.

.PARAMETER OutDir
    Where the step screenshots go. Defaults to <repo>\obj\uia-smoke.

.PARAMETER MenuItemIndex
    Which menu-bar item to expand for the flyout check. Index 2 is the appearance menu in the current
    layout (file, tools, appearance, help). An index rather than a name because the menu text is localised
    and a non-ASCII literal here would be decoded as ANSI and silently never match.

.PARAMETER WaitForWindowMs
    How long to wait for the main window before giving up.

.PARAMETER SkipScreenshots
    Do not capture. Faster, but a failure then has no visual record.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\uia-smoke.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath = '',
    [string]$OutDir = '',
    [int]$MenuItemIndex = 2,
    [int]$WaitForWindowMs = 25000,
    [switch]$SkipScreenshots
)

$ErrorActionPreference = 'Stop'

# Resolved in the body - see the third trap above.
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'bin\x64\Release\net9.0-windows10.0.22621.0\win-x64\SerialPortTool.exe'
}
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot 'obj\uia-smoke'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace UiSmoke -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
'@

$script:failures = @()

function Assert-Check([bool]$condition, [string]$message) {
    if ($condition) {
        Write-Host "  PASS  $message"
    }
    else {
        Write-Host "  FAIL  $message"
        $script:failures += $message
    }
}

<#
    Delivers a real keystroke, and turns a failure to do so into an immediate abort.

    SendWait throws "the operation completed successfully" (a genuinely unhelpful system message) when
    there is no interactive input desktop - a locked session, most obviously. Letting that become a red
    PowerShell error mid-run would leave the remaining assertions to fail for a reason that has nothing to
    do with the app, which is the failure mode this whole script has to avoid.
#>
function Send-Key([string]$keys) {
    try {
        [System.Windows.Forms.SendKeys]::SendWait($keys)
    }
    catch {
        Write-Host ""
        Write-Host "uia-smoke: could not deliver '$keys' - $($_.Exception.Message)"
        Write-Host "           That is an input-session problem (locked desktop, no interactive input),"
        Write-Host "           not app behaviour. Aborting before any assertion can mislead."
        throw 'input session unavailable'
    }
}

function Save-Shot([string]$name) {
    if ($SkipScreenshots) { return }
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
    $gfx = [System.Drawing.Graphics]::FromImage($bmp)
    $gfx.CopyFromScreen($screen.X, $screen.Y, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $gfx.Dispose(); $bmp.Dispose()
    Write-Host "        shot -> $(Join-Path $OutDir $name)"
}

# Leading comma: do not let PowerShell enumerate the collection (see the third trap above).
function Get-ByControlType($root, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return , $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-ListCount($root) {
    return (Get-ByControlType $root ([System.Windows.Automation.ControlType]::List)).Count
}

<#
    Polls the list count until the predicate accepts it, or the timeout expires.

    A single read plus a fixed sleep is what this used to do, and it made the suite FLAKY: removing the
    200 ms-per-step that screenshots happen to add was enough to make "a second F2 closed the palette"
    fail on one run and pass on the next. The cause is not slow key delivery - it is that the palette's
    collapsed ListView leaves the UI Automation tree asynchronously, so the read races the visual tree.
    Polling fixes the cause; a longer sleep would only have narrowed the window.

    Returns the last count observed, so the caller can assert on a real value and print it.
#>
function Wait-ForListCount($root, [scriptblock]$accepts, [int]$timeoutMs = 5000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    $count = Get-ListCount $root

    while (-not (& $accepts $count) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 200
        $count = Get-ListCount $root
    }

    return $count
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Host "uia-smoke: executable not found: $ExePath"
    Write-Host "           build Release first, or pass -ExePath."
    exit 1
}

$proc = $null
try {
    Write-Host "uia-smoke: launching $ExePath"
    $proc = Start-Process -FilePath $ExePath -PassThru

    $deadline = (Get-Date).AddMilliseconds($WaitForWindowMs)
    while ($proc.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline -and -not $proc.HasExited) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
    }

    if ($proc.HasExited) {
        Write-Host "uia-smoke: the app exited during startup (code $($proc.ExitCode))."
        exit 1
    }

    Write-Host ""
    Write-Host "1. window"
    Assert-Check ($proc.MainWindowHandle -ne [IntPtr]::Zero) "a main window appeared within $WaitForWindowMs ms"

    if ($proc.MainWindowHandle -eq [IntPtr]::Zero) {
        Write-Host "uia-smoke: cannot continue without a window."
        exit 1
    }

    Write-Host "        title: $($proc.MainWindowTitle)"

    # Foreground matters: SendKeys delivers to whatever holds focus, and a probe that types into the wrong
    # window would report a failure the app never had.
    [void][UiSmoke.Native]::ShowWindowAsync($proc.MainWindowHandle, 9)
    Start-Sleep -Milliseconds 800
    $broughtForward = [UiSmoke.Native]::SetForegroundWindow($proc.MainWindowHandle)
    Start-Sleep -Milliseconds 800

    Write-Host ""
    Write-Host "0. prerequisites"
    # The return value is CHECKED, not discarded. An earlier version passed it to [void], and when the
    # session locked mid-run the report came back full of app-level findings - "F2 did not open the
    # palette", "F2 did not work with a flyout open" - for a window that had never been brought forward
    # and a keyboard aimed at the lock screen. None of it was about the app. A report like that is worse
    # than no report, because it reads like a discovery.
    $foreground = [UiSmoke.Native]::GetForegroundWindow()
    Assert-Check ($foreground -eq $proc.MainWindowHandle) `
        "the app holds the foreground (SetForegroundWindow returned $broughtForward; foreground is $foreground)"

    if ($foreground -ne $proc.MainWindowHandle) {
        Write-Host ""
        Write-Host "uia-smoke: the app could not be brought to the foreground, so real key input cannot"
        Write-Host "           reach it. Usual causes: the session is locked, or another window is holding"
        Write-Host "           focus. Fix that and re-run - nothing below would have measured the app."
        exit 1
    }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

    Write-Host ""
    Write-Host "2. F2 with focus inside a TextBox"
    $edits = Get-ByControlType $root ([System.Windows.Automation.ControlType]::Edit)
    Assert-Check ($edits.Count -gt 0) "the window exposes at least one Edit control (found $($edits.Count))"

    if ($edits.Count -eq 0) { exit 1 }

    $baseline = Get-ListCount $root
    $edits[0].SetFocus()
    Start-Sleep -Milliseconds 800

    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    Assert-Check ($focused.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit) `
        "focus actually landed in a TextBox (now on '$($focused.Current.Name)')"
    Save-Shot '01-focused-in-textbox.png'

    Send-Key '{F2}'
    Start-Sleep -Milliseconds 300
    $afterFirst = Wait-ForListCount $root { param($count) $count -gt $baseline }
    Assert-Check ($afterFirst -gt $baseline) "F2 opened the palette (list controls $baseline -> $afterFirst)"
    Save-Shot '02-after-first-f2.png'

    Send-Key '{F2}'
    Start-Sleep -Milliseconds 300
    $afterSecond = Wait-ForListCount $root { param($count) $count -eq $baseline }
    Assert-Check ($afterSecond -eq $baseline) `
        "a second F2 closed it again (list controls $afterFirst -> $afterSecond, expected $baseline)"

    Write-Host ""
    Write-Host "3. F2 with a Flyout open"
    $menuBars = Get-ByControlType $root ([System.Windows.Automation.ControlType]::MenuBar)
    Assert-Check ($menuBars.Count -gt 0) "the window exposes a menu bar (found $($menuBars.Count))"

    if ($menuBars.Count -eq 0) { exit 1 }

    $items = @($menuBars[0].FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition))
    Assert-Check ($MenuItemIndex -lt $items.Count) `
        "menu item $MenuItemIndex exists (menu has $($items.Count) items)"

    if ($MenuItemIndex -ge $items.Count) { exit 1 }

    # Selected by index on purpose: the menu items carry localised text, and a non-ASCII literal in this
    # file would be decoded as ANSI and silently never match (see the header).
    $expand = $items[$MenuItemIndex].GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Start-Sleep -Milliseconds 1200

    $withFlyout = Get-ListCount $root
    Save-Shot '03-menu-flyout-open.png'

    Send-Key '{F2}'
    Start-Sleep -Milliseconds 300
    $afterFlyoutF2 = Wait-ForListCount $root { param($count) $count -gt $withFlyout }
    Assert-Check ($afterFlyoutF2 -gt $withFlyout) `
        "F2 opened the palette with a flyout up (list controls $withFlyout -> $afterFlyoutF2)"
    Save-Shot '04-f2-with-flyout-open.png'

    Write-Host ""
    Write-Host "4. the UI thread survived"
    $proc.Refresh()
    Assert-Check (-not $proc.HasExited) "the app is still running"
    Assert-Check $proc.Responding "the UI thread is still responding"
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force
        Write-Host ""
        Write-Host "uia-smoke: app stopped (pid $($proc.Id))"
    }
}

Write-Host ""
if ($script:failures.Count -gt 0) {
    Write-Host "uia-smoke: FAILED - $($script:failures.Count) check(s) failed:"
    foreach ($failure in $script:failures) { Write-Host "  - $failure" }
    exit 1
}

Write-Host "uia-smoke: all checks passed."
exit 0
