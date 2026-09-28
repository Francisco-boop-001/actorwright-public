param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260722-1",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-014-desktop-publish-20260722-5"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-014-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$fixtureRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-014-fixture-20260722-1\Data"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-014-input-$RunTag"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$preflightOutput = Join-Path $inputRoot "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-014-output-$RunTag"
$transactionProposal = Join-Path $outputRoot "selective-paste.proposal.json"
$pluginProposal = Join-Path $outputRoot "plugin.proposal.json"
$outputPlugin = Join-Path $outputRoot "SelectivePasteResult.esp"
$outputPreset = Join-Path $outputRoot "SelectivePasteResult.jslot"
$sourcePlugin = Join-Path $dataRoot "SelectivePasteFixtureSSE.esp"
$sourcePreset = Join-Path $dataRoot "source.jslot"
$targetPreset = Join-Path $dataRoot "target.jslot"
$skyrimMasterSource = "F:\ExampleGame\Game Root\Data\Skyrim.esm"
$raceMenuSource = Join-Path $projectRoot `
    "01-source-copies\racemenu-paint-catalog-real\Data"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_sky_gui_014_selective_output.py"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-014-selective-output-raw-audit-$RunTag.json"
$currentNpcScreenshot = Join-Path $labRoot "Resources\Screenshot\NpcStudioEmiTrial.png"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-loaded-$RunTag.png"
    allSelected = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-all-selected-$RunTag.png"
    noneSelected = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-none-selected-$RunTag.png"
    noOp = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-no-op-$RunTag.png"
    escapeCancel = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-escape-cancel-$RunTag.png"
    titleClose = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-title-close-$RunTag.png"
    partial = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-partial-selection-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-reviewed-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-014-verified-$RunTag.png"
}

$required = @(
    $executable,
    (Join-Path $fixtureRoot "SelectivePasteFixtureSSE.esp"),
    (Join-Path $fixtureRoot "source.jslot"),
    (Join-Path $fixtureRoot "target.jslot"),
    $skyrimMasterSource,
    (Join-Path $raceMenuSource "RaceMenu.esp"),
    (Join-Path $raceMenuSource "RaceMenu.bsa"),
    $rawVerifier,
    $currentNpcScreenshot
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required acceptance input is absent: $path"
    }
}
foreach ($path in @($inputRoot, $outputRoot)) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance path exists and will not be overwritten: $path"
    }
}
foreach ($path in @($reportPathFull, $rawReport) + $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence exists and will not be overwritten: $path"
    }
}
if (-not $reportPathFull.StartsWith(
        $projectRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Acceptance report escaped the project root."
}

New-Item -ItemType Directory -Path $dataRoot | Out-Null
New-Item -ItemType Directory -Path $outputRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $fixtureRoot "SelectivePasteFixtureSSE.esp") `
    -Destination $sourcePlugin
Copy-Item -LiteralPath (Join-Path $fixtureRoot "source.jslot") `
    -Destination $sourcePreset
Copy-Item -LiteralPath (Join-Path $fixtureRoot "target.jslot") `
    -Destination $targetPreset
Copy-Item -LiteralPath $skyrimMasterSource `
    -Destination (Join-Path $dataRoot "Skyrim.esm")
Copy-Item -LiteralPath (Join-Path $raceMenuSource "RaceMenu.esp") `
    -Destination (Join-Path $dataRoot "RaceMenu.esp")
Copy-Item -LiteralPath (Join-Path $raceMenuSource "RaceMenu.bsa") `
    -Destination (Join-Path $dataRoot "RaceMenu.bsa")

$expectedHashes = [ordered]@{
    "SelectivePasteFixtureSSE.esp" = `
        "3125CB1B23BFF48A3B771F936F66B0CC2897F612E3CFFBD3A97B7E63356D8B3D"
    "source.jslot" = `
        "67C8D9FCF2D77249EABAE50588E55BC16A093DA759931493F44D310611D7C408"
    "target.jslot" = `
        "D20B36750D2077F6F5F8F3CE6C1216DFEC558493AFEFC63C3804E9C7A87FCC78"
    "Skyrim.esm" = `
        "06A9881F6AB277AFD2A82E71F8A3719183B7031DB4D9C78A7F40C08E1E4FFA91"
    "RaceMenu.esp" = `
        "15E009B7F219B1E95BD78A31CDF8BE2D3B45A38B784DD04F34A71A4AB6F76602"
    "RaceMenu.bsa" = `
        "FCC46F42731D2B7C3782B96CFF40930FFCF689482CA8E9A5568CF2B76E9A7603"
}
foreach ($entry in $expectedHashes.GetEnumerator()) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $dataRoot $entry.Key) `
        -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) {
        throw "Copied acceptance input hash mismatch for $($entry.Key)."
    }
}
$loadOrder = @{
    schemaVersion = 1
    edition = "skyrimse"
    plugins = @(
        @{ name = "Skyrim.esm"; order = 0; enabled = $true },
        @{ name = "SelectivePasteFixtureSSE.esp"; order = 1; enabled = $true },
        @{ name = "RaceMenu.esp"; order = 2; enabled = $true }
    )
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $loadOrderPath, $loadOrder, [Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui014Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr destination, int destinationX, int destinationY,
        int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(
        IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static string[] VisibleWindowsForProcess(uint wantedProcessId)
    {
        var rows = new List<string>();
        EnumWindows((handle, ignored) =>
        {
            uint processId;
            GetWindowThreadProcessId(handle, out processId);
            if (processId == wantedProcessId && IsWindowVisible(handle))
            {
                var title = new StringBuilder(512);
                GetWindowText(handle, title, title.Capacity);
                rows.Add(handle.ToInt64() + "|" + title);
            }
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
'@

function Wait-Until {
    param([scriptblock]$Condition, [string]$Failure, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 125
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function New-ElementCondition {
    param([System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    return New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
}

function Find-Element {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    $element = Try-FindElement $Root $ControlType $Name
    if ($null -eq $element) { throw "Required element '$Name' was not found." }
    return $element
}

function Try-FindElement {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ElementCondition $ControlType $Name))
}

function Invoke-Button {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $button = Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) { throw "Required button '$Name' is disabled." }
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Press-Button {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [IntPtr]$WindowHandle)
    $button = Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) { throw "Required button '$Name' is disabled." }
    [void][SkyGui014Native]::ShowWindow($WindowHandle, 9)
    [void][SkyGui014Native]::SetForegroundWindow($WindowHandle)
    Start-Sleep -Milliseconds 180
    $point = $button.GetClickablePoint()
    if (-not [SkyGui014Native]::SetCursorPos(
            [int][Math]::Round($point.X), [int][Math]::Round($point.Y))) {
        throw "Could not move to the clickable point for '$Name'."
    }
    [SkyGui014Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui014Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Select-Tab {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $tab = Find-Element $Root ([System.Windows.Automation.ControlType]::TabItem) $Name
    ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Wait-Until -Failure "Tab '$Name' did not become selected." -Condition {
        ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    }
    Start-Sleep -Milliseconds 180
}

function Set-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [string]$Value)
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
    Wait-Until -Failure "Edit '$Name' did not retain the requested value." -Condition {
        ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $Value
    }
}

function Test-TextContains {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Needle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($item in $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($item.Current.Name.IndexOf(
            $Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

function Toggle-Checkbox {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $checkbox = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    ([System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)).Toggle()
}

function Test-CheckboxStateCount {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ToggleState]$State, [int]$Expected)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::CheckBox)
    $matches = 0
    foreach ($item in $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $toggle = [System.Windows.Automation.TogglePattern]$item.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -eq $State) { $matches++ }
    }
    return $matches -eq $Expected
}

function Set-AllCheckboxes {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ToggleState]$State)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::CheckBox)
    foreach ($item in $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $toggle = [System.Windows.Automation.TogglePattern]$item.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne $State) { $toggle.Toggle() }
    }
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $rect = New-Object SkyGui014Native+Rect
    foreach ($ignored in 1..20) {
        [void][SkyGui014Native]::ShowWindow($Handle, 9)
        [void][SkyGui014Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui014Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui014Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui014Native]::IsIconic($Handle) -and
            ($rect.Right - $rect.Left) -ge 640 -and
            ($rect.Bottom - $rect.Top) -ge 400) { break }
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 640 -or $height -lt 400) {
        throw "Screenshot target did not restore ($width x $height)."
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            $screenContext = [SkyGui014Native]::GetDC([IntPtr]::Zero)
            try {
                if (-not [SkyGui014Native]::PrintWindow(
                        $Handle, $deviceContext, 2)) {
                    if ($screenContext -eq [IntPtr]::Zero -or
                        -not [SkyGui014Native]::BitBlt(
                            $deviceContext, 0, 0, $width, $height,
                            $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui014Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
                }
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Start-OwnedModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$OwnerHandle,
        [string]$ButtonName, [string]$WindowTitlePrefix,
        [int]$TimeoutSeconds = 120)
    $escapedButton = $ButtonName.Replace("'", "''")
    $helperId = [Guid]::NewGuid().ToString("N")
    $helperOut = Join-Path $outputRoot "modal-helper-$helperId.out.log"
    $helperError = Join-Path $outputRoot "modal-helper-$helperId.error.log"
    $escapedHelperError = $helperError.Replace("'", "''")
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    [IntPtr]$($OwnerHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '$escapedButton')))
`$button = `$root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'Modal opener absent or disabled.'
}
([System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 5
}
catch {
    [IO.File]::WriteAllText(
        '$escapedHelperError', (`$_ | Out-String), [Text.UTF8Encoding]::new(`$false))
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    try {
        $script:modalHandle = [IntPtr]::Zero
        Wait-Until -Failure "Owned modal '$WindowTitlePrefix' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            if ($Application.HasExited) { return $true }
            if ($helper.HasExited -and $helper.ExitCode -ne 0) { return $true }
            foreach ($row in [SkyGui014Native]::VisibleWindowsForProcess(
                    [uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                $candidateRect = New-Object SkyGui014Native+Rect
                $hasUsableBounds = [SkyGui014Native]::GetWindowRect(
                    $candidate, [ref]$candidateRect) -and
                    ($candidateRect.Right - $candidateRect.Left) -ge 600 -and
                    ($candidateRect.Bottom - $candidateRect.Top) -ge 400
                if ($candidate -ne $OwnerHandle -and $hasUsableBounds) {
                    $script:modalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        if ($Application.HasExited) {
            throw "The packaged application exited while opening the selective-paste modal; " +
                "exit=$($Application.ExitCode)."
        }
        if ($script:modalHandle -eq [IntPtr]::Zero) {
            $helperStdout = if (Test-Path -LiteralPath $helperOut) {
                Get-Content -LiteralPath $helperOut -Raw
            } else { "" }
            $helperStderr = if (Test-Path -LiteralPath $helperError) {
                Get-Content -LiteralPath $helperError -Raw
            } else { "" }
            throw "Modal helper exited before a usable dialog appeared. " +
                "exit=$($helper.ExitCode) stdout=$helperStdout stderr=$helperStderr"
        }
        return [pscustomobject]@{
            Handle = $script:modalHandle
            Root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $script:modalHandle)
            Helper = $helper
            HelperOut = $helperOut
            HelperError = $helperError
        }
    }
    catch {
        if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
        throw
    }
}

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper, [int]$TimeoutSeconds = 45)
    Wait-Until -Failure "Owned modal did not close." -TimeoutSeconds $TimeoutSeconds `
        -Condition {
        -not ([SkyGui014Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Get-RelativeLabPath {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($labRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not below the lab root: $full"
    }
    return $full.Substring($labRoot.Length + 1).Replace("\", "/")
}

$application = $null
$activeHelpers = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $application = Start-Process -FilePath $executable -PassThru
    $script:mainHandle = [IntPtr]::Zero
    Wait-Until -Failure "The packaged desktop did not create a usable main window." `
        -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui014Native]::VisibleWindowsForProcess(
                [uint32]$application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            $candidateRect = New-Object SkyGui014Native+Rect
            if ([SkyGui014Native]::GetWindowRect($candidate, [ref]$candidateRect) -and
                ($candidateRect.Right - $candidateRect.Left) -ge 800 -and
                ($candidateRect.Bottom - $candidateRect.Top) -ge 600) {
                $script:mainHandle = $candidate
                return $true
            }
        }
        return $false
    }
    [void]$application.WaitForInputIdle(10000)
    [void][SkyGui014Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui014Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Wait-Until -Failure "The packaged shell did not finish its automation tree." `
        -TimeoutSeconds 120 -Condition {
        $null -ne (Try-FindElement $mainRoot `
            ([System.Windows.Automation.ControlType]::TabItem) `
            "Open copied Skyrim workspace task")
    }

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The selective-paste task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Paste selected Skyrim appearance task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Paste selected Skyrim appearance task"
    Set-EditValue $mainRoot "Selective paste source plugin" `
        "SelectivePasteFixtureSSE.esp"
    Set-EditValue $mainRoot "Selective paste source NPC FormID" "0x00000900"
    Set-EditValue $mainRoot "Selective paste source preset" $sourcePreset
    Set-EditValue $mainRoot "Selective paste target plugin" `
        "SelectivePasteFixtureSSE.esp"
    Set-EditValue $mainRoot "Selective paste target NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Selective paste target preset" $targetPreset
    Set-EditValue $mainRoot "Selective paste transaction proposal" $transactionProposal
    Set-EditValue $mainRoot "Selective paste plugin proposal" $pluginProposal
    Set-EditValue $mainRoot "Selective paste output plugin" $outputPlugin
    Set-EditValue $mainRoot "Selective paste output preset" $outputPreset
    Invoke-Button $mainRoot "Load selective appearance source and target"
    Wait-Until -Failure "The two exact selective-paste endpoints did not load." `
        -TimeoutSeconds 180 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open selective appearance category chooser").Current.IsEnabled -and
            (Test-TextContains $mainRoot "Loaded without selection")
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.twoCarrierLoad = $true

    $allModal = Start-OwnedModal $application $mainHandle `
        "Open selective appearance category chooser" "Paste selected appearance" 180
    $activeHelpers.Add($allModal.Helper)
    Capture-Window $allModal.Handle $screenshots.allSelected
    $checkboxCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::CheckBox)
    $checkboxDebug = @($allModal.Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $checkboxCondition) |
        ForEach-Object {
            $toggle = [System.Windows.Automation.TogglePattern]$_.GetCurrentPattern(
                [System.Windows.Automation.TogglePattern]::Pattern)
            "$($_.Current.Name)=$($toggle.Current.ToggleState)"
        }) -join ";"
    Write-Output "SKY-GUI-014 modal='$($allModal.Root.Current.Name)' checkboxes='$checkboxDebug'"
    Wait-Until -Failure "The modal did not default to ten checked categories." -Condition {
        Test-CheckboxStateCount $allModal.Root `
            ([System.Windows.Automation.ToggleState]::On) 10
    }
    Set-AllCheckboxes $allModal.Root ([System.Windows.Automation.ToggleState]::Off)
    Wait-Until -Failure "Select None did not clear all categories." -Condition {
        Test-CheckboxStateCount $allModal.Root `
            ([System.Windows.Automation.ToggleState]::Off) 10
    }
    Capture-Window $allModal.Handle $screenshots.noneSelected
    Set-AllCheckboxes $allModal.Root ([System.Windows.Automation.ToggleState]::On)
    Wait-Until -Failure "Select All did not restore all categories." -Condition {
        Test-CheckboxStateCount $allModal.Root `
            ([System.Windows.Automation.ToggleState]::On) 10
    }
    [void][SkyGui014Native]::PostMessage(
        $allModal.Handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    [void][SkyGui014Native]::PostMessage(
        $allModal.Handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-ModalClosed $application $allModal.Handle $allModal.Helper
    Wait-Until -Failure "Cancel staged a category selection." -Condition {
        Test-TextContains $mainRoot "No accepted category selection"
    }
    $checks.selectAllNoneAndCancel = $true

    $noneModal = Start-OwnedModal $application $mainHandle `
        "Open selective appearance category chooser" "Paste selected appearance" 180
    $activeHelpers.Add($noneModal.Helper)
    Set-AllCheckboxes $noneModal.Root ([System.Windows.Automation.ToggleState]::Off)
    [void][SkyGui014Native]::SetForegroundWindow($noneModal.Handle)
    [void][SkyGui014Native]::PostMessage(
        $noneModal.Handle, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero)
    [void][SkyGui014Native]::PostMessage(
        $noneModal.Handle, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero)
    Wait-ModalClosed $application $noneModal.Handle $noneModal.Helper
    Wait-Until -Failure "Enter did not accept Select None as a clean no-op." -Condition {
        Test-TextContains $mainRoot "Select None accepted: clean no-op"
    }
    if ((Test-Path -LiteralPath $transactionProposal) -or
        (Test-Path -LiteralPath $pluginProposal) -or
        (Test-Path -LiteralPath $outputPlugin) -or
        (Test-Path -LiteralPath $outputPreset)) {
        throw "Select None created a proposal or output carrier."
    }
    Capture-Window $mainHandle $screenshots.noOp
    $checks.enterNoOpNoWrite = $true

    $escapeModal = Start-OwnedModal $application $mainHandle `
        "Open selective appearance category chooser" "Paste selected appearance" 180
    $activeHelpers.Add($escapeModal.Helper)
    [void][SkyGui014Native]::PostMessage(
        $escapeModal.Handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    [void][SkyGui014Native]::PostMessage(
        $escapeModal.Handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-ModalClosed $application $escapeModal.Handle $escapeModal.Helper
    Wait-Until -Failure "Escape changed the parent's clean no-op state." -Condition {
        Test-TextContains $mainRoot "Select None accepted: clean no-op"
    }
    Capture-Window $mainHandle $screenshots.escapeCancel
    $checks.escapeRollback = $true

    $closeModal = Start-OwnedModal $application $mainHandle `
        "Open selective appearance category chooser" "Paste selected appearance" 180
    $activeHelpers.Add($closeModal.Helper)
    [void][SkyGui014Native]::PostMessage(
        $closeModal.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeModal.Handle $closeModal.Helper
    Wait-Until -Failure "Title-bar close changed the parent's clean no-op state." -Condition {
        Test-TextContains $mainRoot "Select None accepted: clean no-op"
    }
    Capture-Window $mainHandle $screenshots.titleClose
    $checks.titleBarCloseRollback = $true

    $partialModal = Start-OwnedModal $application $mainHandle `
        "Open selective appearance category chooser" "Paste selected appearance" 180
    $activeHelpers.Add($partialModal.Helper)
    [void][SkyGui014Native]::ShowWindow($partialModal.Handle, 3)
    [void][SkyGui014Native]::SetForegroundWindow($partialModal.Handle)
    $spaceToggle = Find-Element $partialModal.Root `
        ([System.Windows.Automation.ControlType]::CheckBox) "RaceMenu paints and skin"
    $spaceToggle.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait(" ")
    Start-Sleep -Milliseconds 200
    foreach ($title in @("Hair color", "Face tints", "Face morphs")) {
        Toggle-Checkbox $partialModal.Root $title
    }
    Wait-Until -Failure "The partial selection did not retain six checked categories." -Condition {
        Test-CheckboxStateCount $partialModal.Root `
            ([System.Windows.Automation.ToggleState]::On) 6
    }
    Capture-Window $partialModal.Handle $screenshots.partial
    [void][SkyGui014Native]::PostMessage(
        $partialModal.Handle, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero)
    [void][SkyGui014Native]::PostMessage(
        $partialModal.Handle, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero)
    Wait-ModalClosed $application $partialModal.Handle $partialModal.Helper
    Wait-Until -Failure "The accepted six-category selection did not reach the parent." -Condition {
        Test-TextContains $mainRoot "6 category choices accepted"
    }
    $checks.spaceToggleAndPartialPaste = $true

    if ((Test-Path -LiteralPath $transactionProposal) -or
        (Test-Path -LiteralPath $pluginProposal) -or
        (Test-Path -LiteralPath $outputPlugin) -or
        (Test-Path -LiteralPath $outputPreset)) {
        throw "Category selection wrote before explicit review."
    }
    Invoke-Button $mainRoot "Review selective appearance proposals"
    Wait-Until -Failure "The two proposals did not become current." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $transactionProposal) -and
            (Test-Path -LiteralPath $pluginProposal) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    if ((Test-Path -LiteralPath $outputPlugin) -or
        (Test-Path -LiteralPath $outputPreset)) {
        throw "Proposal review wrote an output carrier early."
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.reviewWritesProposalsOnly = $true

    Invoke-Button $mainRoot "Write and verify selective appearance outputs"
    Wait-Until -Failure "The verified ESP/jslot pair was not retained." `
        -TimeoutSeconds 240 -Condition {
        (Test-Path -LiteralPath $outputPlugin) -and
            (Test-Path -LiteralPath $outputPreset) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verified
    $checks.atomicWriteAndSecondReadback = $true

    $rawText = (& python $rawVerifier `
        --source-plugin $sourcePlugin `
        --source-preset $sourcePreset `
        --target-preset $targetPreset `
        --output-plugin $outputPlugin `
        --output-preset $outputPreset `
        --proposal $transactionProposal `
        --report $rawReport 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent raw selective-output verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed -or
        -not $rawAudit.selected_preset_carriers_match_source -or
        -not $rawAudit.unchecked_preset_carriers_match_target) {
        throw "Independent raw audit did not prove selected and unchecked carriers."
    }
    $checks.independentRawPluginAndJsonAudit = $true

    $screenshotEvidence = foreach ($entry in $screenshots.GetEnumerator()) {
        $item = Get-Item -LiteralPath $entry.Value
        [ordered]@{
            state = $entry.Key
            path = Get-RelativeLabPath $item.FullName
            size = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName `
                -Algorithm SHA256).Hash
        }
    }
    $evidence = [ordered]@{
        result = "PASS"
        surfaceIds = @("SKY-GUI-014")
        runTag = $RunTag
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable `
            -Algorithm SHA256).Hash
        copiedInput = [ordered]@{
            dataRoot = Get-RelativeLabPath $dataRoot
            sourcePlugin = Get-RelativeLabPath $sourcePlugin
            sourcePluginSha256 = $expectedHashes["SelectivePasteFixtureSSE.esp"]
            sourceNpcFormId = "0x00000900"
            targetNpcFormId = "0x00000800"
            sourcePreset = Get-RelativeLabPath $sourcePreset
            sourcePresetSha256 = $expectedHashes["source.jslot"]
            targetPreset = Get-RelativeLabPath $targetPreset
            targetPresetSha256 = $expectedHashes["target.jslot"]
        }
        selection = @(
            "body-weight", "body-shape", "outfits", "face-parts", "sculpt", "chargen-flag"
        )
        proposal = [ordered]@{
            transaction = Get-RelativeLabPath $transactionProposal
            transactionSha256 = (Get-FileHash -LiteralPath $transactionProposal `
                -Algorithm SHA256).Hash
            plugin = Get-RelativeLabPath $pluginProposal
            pluginSha256 = (Get-FileHash -LiteralPath $pluginProposal `
                -Algorithm SHA256).Hash
            wroteOutputsDuringReview = $false
        }
        output = [ordered]@{
            plugin = Get-RelativeLabPath $outputPlugin
            pluginSha256 = (Get-FileHash -LiteralPath $outputPlugin `
                -Algorithm SHA256).Hash
            preset = Get-RelativeLabPath $outputPreset
            presetSha256 = (Get-FileHash -LiteralPath $outputPreset `
                -Algorithm SHA256).Hash
            sourceOwnedNpcOverrideCount = 1
            selfOwnedNpcTargetCount = 0
            wrldCount = 0
            cellCount = 0
            vmadCount = 0
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        rawAudit = Get-RelativeLabPath $rawReport
        qualitativeCurrentNpcScreenshot = [ordered]@{
            path = Get-RelativeLabPath $currentNpcScreenshot
            sha256 = (Get-FileHash -LiteralPath $currentNpcScreenshot `
                -Algorithm SHA256).Hash
            demonstrates = "the current NPC mod renders in game with intact gross geometry"
            providerBoundToSyntheticSelectivePasteFixture = $false
            inFrameControlNpc = $false
        }
        faceGenAuthority = $false
        bodySlideBuildAuthority = $false
        runtimeAuthority = $false
        visualAuthority = $false
        protectedLiveRootReadOnlySource = $skyrimMasterSource
        protectedLiveRootModified = $false
    }
    $json = $evidence | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText(
        $reportPathFull, $json + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output $json
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui014Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
