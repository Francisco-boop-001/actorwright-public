param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260723-1",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-017-desktop-publish-20260723-1"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-017-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$fixtureRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-016-fixture-20260723-1"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-017-input-$RunTag"
$phase1Root = Join-Path $inputRoot "phase1"
$dataRoot = Join-Path $phase1Root "Data"
$loadOrderPath = Join-Path $phase1Root "load-order.json"
$preflightOutput = Join-Path $phase1Root "future-output"
$phase2Root = Join-Path $inputRoot "phase2"
$phase2DataRoot = Join-Path $phase2Root "Data"
$phase2LoadOrderPath = Join-Path $phase2Root "load-order.json"
$phase2PreflightOutput = Join-Path $phase2Root "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-017-output-$RunTag"
$leveledProposal = Join-Path $outputRoot `
    "CreatedList.leveled-list-production-proposal.json"
$leveledPlugin = Join-Path $outputRoot "CreatedList.esp"
$outfitProposal = Join-Path $outputRoot "generated-list.outfit-proposal.json"
$outfitPlugin = Join-Path $outputRoot "GeneratedListOutfit.esp"
$sourceProvider = Join-Path $phase2DataRoot "OutfitProviderAfter.esp"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_sky_gui_017_leveled_entries.py"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-017-leveled-list-raw-audit-$RunTag.json"
$negativeControlRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-016-output-20260723-5"
$negativeRawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-017-negative-control-$RunTag.json"
$currentNpcScreenshot = Join-Path $labRoot "Resources\Screenshot\NpcStudioEmiTrial.png"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-loaded-$RunTag.png"
    header = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-header-$RunTag.png"
    entry = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-entry-$RunTag.png"
    invalid = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-invalid-$RunTag.png"
    boundary = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-boundary-$RunTag.png"
    edit = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-edit-$RunTag.png"
    finalRows = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-final-rows-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-verified-$RunTag.png"
    outfitCancelled = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-outfit-cancelled-$RunTag.png"
    outfit = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-017-outfit-$RunTag.png"
}

$required = @(
    $executable,
    (Join-Path $fixtureRoot "phase1\Data\OutfitBase.esm"),
    (Join-Path $fixtureRoot "phase1\Data\OutfitProvider.esp"),
    (Join-Path $fixtureRoot "phase1\load-order.json"),
    (Join-Path $fixtureRoot "phase2\Data\OutfitBase.esm"),
    (Join-Path $fixtureRoot "phase2\Data\OutfitProviderAfter.esp"),
    (Join-Path $fixtureRoot "phase2\load-order.json"),
    (Join-Path $fixtureRoot "expected.json"),
    $rawVerifier,
    (Join-Path $negativeControlRoot "CreatedList.esp"),
    (Join-Path $negativeControlRoot `
        "CreatedList.leveled-list-production-proposal.json"),
    (Join-Path $negativeControlRoot "GeneratedListOutfit.esp"),
    (Join-Path $negativeControlRoot "generated-list.outfit-proposal.json"),
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
foreach ($path in @($reportPathFull, $rawReport, $negativeRawReport) + $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence exists and will not be overwritten: $path"
    }
}
if (-not $reportPathFull.StartsWith(
        $projectRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Acceptance report escaped the project root."
}

New-Item -ItemType Directory -Path $dataRoot | Out-Null
New-Item -ItemType Directory -Path $phase2DataRoot | Out-Null
New-Item -ItemType Directory -Path $outputRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase1\Data\OutfitBase.esm") `
    -Destination (Join-Path $dataRoot "OutfitBase.esm")
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase1\Data\OutfitProvider.esp") `
    -Destination (Join-Path $dataRoot "OutfitProvider.esp")
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase1\load-order.json") `
    -Destination $loadOrderPath
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase2\Data\OutfitBase.esm") `
    -Destination (Join-Path $phase2DataRoot "OutfitBase.esm")
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase2\Data\OutfitProviderAfter.esp") `
    -Destination $sourceProvider
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase2\load-order.json") `
    -Destination $phase2LoadOrderPath
$expected = Get-Content -LiteralPath (Join-Path $fixtureRoot "expected.json") `
    -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $dataRoot "OutfitBase.esm") `
        -Algorithm SHA256).Hash -ne $expected.phase1BaseSha256.ToUpperInvariant() -or
    (Get-FileHash -LiteralPath (Join-Path $dataRoot "OutfitProvider.esp") `
        -Algorithm SHA256).Hash -ne $expected.phase1ProviderSha256.ToUpperInvariant() -or
    (Get-FileHash -LiteralPath $sourceProvider `
        -Algorithm SHA256).Hash -ne $expected.phase2ProviderSha256.ToUpperInvariant()) {
    throw "Copied LVLI fixture hashes do not match the frozen fixture contract."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui017Native
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
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height, bool repaint);
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

function Try-FindElement {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ElementCondition $ControlType $Name))
}

function Find-Element {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    $element = Try-FindElement $Root $ControlType $Name
    if ($null -eq $element) { throw "Required element '$Name' was not found." }
    return $element
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
    [void][SkyGui017Native]::ShowWindow($WindowHandle, 9)
    [void][SkyGui017Native]::SetForegroundWindow($WindowHandle)
    $button.SetFocus()
    Start-Sleep -Milliseconds 180
    $point = $button.GetClickablePoint()
    if (-not [SkyGui017Native]::SetCursorPos(
            [int][Math]::Round($point.X), [int][Math]::Round($point.Y))) {
        throw "Could not move to the clickable point for '$Name'."
    }
    [SkyGui017Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui017Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
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

function Get-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    return ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}

function Test-ButtonEnabled {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    return (Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Button) $Name).Current.IsEnabled
}

function Set-RangeValue {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [double]$Value)
    $slider = Find-Element $Root ([System.Windows.Automation.ControlType]::Slider) $Name
    $pattern = [System.Windows.Automation.RangeValuePattern]$slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $pattern.SetValue($Value)
    Wait-Until -Failure "Slider '$Name' did not retain the requested value." -Condition {
        [Math]::Abs((
            [System.Windows.Automation.RangeValuePattern]$slider.GetCurrentPattern(
                [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value - $Value) -lt 0.01
    }
}

function Set-ToggleOn {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $toggle = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $pattern = [System.Windows.Automation.TogglePattern]$toggle.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($pattern.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::On) { $pattern.Toggle() }
    Wait-Until -Failure "Checkbox '$Name' did not become checked." -Condition {
        ([System.Windows.Automation.TogglePattern]$toggle.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq
            [System.Windows.Automation.ToggleState]::On
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

function Find-ItemContaining {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [string]$Needle)
    $containers = @()
    $list = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::List) $ContainerName
    if ($null -ne $list) { $containers += $list }
    $grid = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::DataGrid) $ContainerName
    if ($null -ne $grid) { $containers += $grid }
    if ($containers.Count -ne 1) {
        throw "Container '$ContainerName' was absent or ambiguous."
    }
    $trueCondition = [System.Windows.Automation.Condition]::TrueCondition
    foreach ($item in $containers[0].FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $trueCondition)) {
        if (($item.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem -or
             $item.Current.ControlType -eq [System.Windows.Automation.ControlType]::DataItem) -and
            $item.Current.Name.IndexOf(
                $Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $item
        }
    }
    throw "Item containing '$Needle' was not found in '$ContainerName'."
}

function Select-ItemContaining {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [string]$Needle)
    $item = Find-ItemContaining $Root $ContainerName $Needle
    ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 150
}

function Get-ListItems {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName)
    $list = Find-Element $Root ([System.Windows.Automation.ControlType]::List) `
        $ContainerName
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($list.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Select-ListItemAt {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [int]$Index)
    $items = @(Get-ListItems $Root $ContainerName)
    if ($Index -lt 0 -or $Index -ge $items.Count) {
        throw "List '$ContainerName' has $($items.Count) rows; index $Index is unavailable."
    }
    ([System.Windows.Automation.SelectionItemPattern]$items[$Index].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 150
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $rect = New-Object SkyGui017Native+Rect
    foreach ($ignored in 1..20) {
        [void][SkyGui017Native]::ShowWindow($Handle, 9)
        [void][SkyGui017Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui017Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui017Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui017Native]::IsIconic($Handle) -and
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
            $screenContext = [SkyGui017Native]::GetDC([IntPtr]::Zero)
            try {
                if (-not [SkyGui017Native]::PrintWindow(
                        $Handle, $deviceContext, 2)) {
                    if ($screenContext -eq [IntPtr]::Zero -or
                        -not [SkyGui017Native]::BitBlt(
                            $deviceContext, 0, 0, $width, $height,
                            $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui017Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
                }
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Resize-Window {
    param([IntPtr]$Handle, [int]$Width, [int]$Height)
    $rect = New-Object SkyGui017Native+Rect
    if (-not [SkyGui017Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read the modal bounds before resize."
    }
    if (-not [SkyGui017Native]::MoveWindow(
            $Handle, $rect.Left, $rect.Top, $Width, $Height, $true)) {
        throw "The packaged modal refused a user-size operation."
    }
    Wait-Until -Failure "The packaged modal did not retain its resized bounds." -Condition {
        $current = New-Object SkyGui017Native+Rect
        [SkyGui017Native]::GetWindowRect($Handle, [ref]$current) -and
            ($current.Right - $current.Left) -eq $Width -and
            ($current.Bottom - $current.Top) -eq $Height
    }
}

function Open-OutfitModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [System.Windows.Automation.AutomationElement]$MainRoot)
    $helperError = Join-Path $outputRoot `
        ("outfit-modal-helper-" + [Guid]::NewGuid().ToString("N") + ".error.log")
    $escapedError = $helperError.Replace("'", "''")
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    [IntPtr]$($MainHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Open Skyrim outfit workbench')))
`$button = `$root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'Outfit workbench opener absent or disabled.'
}

([System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
catch {
    [IO.File]::WriteAllText(
        '$escapedError', (`$_ | Out-String), [Text.UTF8Encoding]::new(`$false))
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    $script:activeHelpers.Add($helper)
    $script:modalHandle = [IntPtr]::Zero
    Wait-Until -Failure "The outfit workbench did not become visible." `
        -TimeoutSeconds 120 -Condition {
        if ($helper.HasExited -and $helper.ExitCode -ne 0) { return $true }
        foreach ($row in [SkyGui017Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            if ($candidate -eq $MainHandle) { continue }
            $rect = New-Object SkyGui017Native+Rect
            if ([SkyGui017Native]::GetWindowRect($candidate, [ref]$rect) -and
                ($rect.Right - $rect.Left) -ge 900 -and
                ($rect.Bottom - $rect.Top) -ge 600) {
                $script:modalHandle = $candidate
                return $true
            }
        }
        return $false
    }
    if ($script:modalHandle -eq [IntPtr]::Zero) {
        $detail = if (Test-Path -LiteralPath $helperError) {
            Get-Content -LiteralPath $helperError -Raw
        } else { "helper exit=$($helper.ExitCode)" }
        throw "The outfit modal helper failed: $detail"
    }
    return [pscustomobject]@{
        Handle = $script:modalHandle
        Root = [System.Windows.Automation.AutomationElement]::FromHandle(
            $script:modalHandle)
        Helper = $helper
    }
}

function Open-LeveledHeaderModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [System.Windows.Automation.AutomationElement]$MainRoot)
    $helperError = Join-Path $outputRoot `
        ("leveled-modal-helper-" + [Guid]::NewGuid().ToString("N") + ".error.log")
    $escapedError = $helperError.Replace("'", "''")
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    [IntPtr]$($MainHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Open leveled-list header and entry editors')))
`$button = `$root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'Leveled-list editor opener absent or disabled.'
}
([System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
catch {
    [IO.File]::WriteAllText(
        '$escapedError', (`$_ | Out-String), [Text.UTF8Encoding]::new(`$false))
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    $script:activeHelpers.Add($helper)
    $modal = Wait-ProcessWindowByTitle $Application $MainHandle "New leveled list" `
        "The leveled-list header editor did not become visible."
    if ($null -eq $modal) {
        $detail = if (Test-Path -LiteralPath $helperError) {
            Get-Content -LiteralPath $helperError -Raw
        } else { "helper exit=$($helper.ExitCode)" }
        throw "The leveled-list modal helper failed: $detail"
    }
    return [pscustomobject]@{
        Handle = $modal.Handle
        Root = $modal.Root
        Helper = $helper
    }
}

function Open-ButtonModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [System.Windows.Automation.AutomationElement]$MainRoot,
        [string]$ButtonName, [string]$TitleNeedle)
    $helperError = Join-Path $outputRoot `
        ("child-modal-helper-" + [Guid]::NewGuid().ToString("N") + ".error.log")
    $escapedError = $helperError.Replace("'", "''")
    $escapedButton = $ButtonName.Replace("'", "''")
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    [IntPtr]$($MainHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        '$escapedButton')))
`$button = `$root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'The requested child editor opener is absent or disabled.'
}
([System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
catch {
    [IO.File]::WriteAllText(
        '$escapedError', (`$_ | Out-String), [Text.UTF8Encoding]::new(`$false))
    exit 1
}
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    $script:activeHelpers.Add($helper)
    $modal = Wait-ProcessWindowByTitle $Application $MainHandle $TitleNeedle `
        "The '$TitleNeedle' child editor did not become visible."
    if ($null -eq $modal) {
        $detail = if (Test-Path -LiteralPath $helperError) {
            Get-Content -LiteralPath $helperError -Raw
        } else { "helper exit=$($helper.ExitCode)" }
        throw "The child modal helper failed: $detail"
    }
    return [pscustomobject]@{
        Handle = $modal.Handle
        Root = $modal.Root
        Helper = $helper
    }
}

function Wait-ProcessWindowByTitle {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [string]$TitleNeedle, [string]$Failure)
    $script:foundModal = $null
    Wait-Until -Failure $Failure -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui017Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            if ($candidate -eq $MainHandle -or
                $parts[1].IndexOf($TitleNeedle,
                    [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            $script:foundModal = [pscustomobject]@{
                Handle = $candidate
                Root = [System.Windows.Automation.AutomationElement]::FromHandle($candidate)
            }
            return $true
        }
        return $false
    }
    return $script:foundModal
}

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper)
    Wait-Until -Failure "The outfit workbench did not close." -TimeoutSeconds 45 `
        -Condition {
        -not ([SkyGui017Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The outfit modal opener remained blocked after close."
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
$checks = [ordered]@{}
$activeHelpers = New-Object `
    System.Collections.Generic.List[System.Diagnostics.Process]
try {
    $negativeText = (& python $rawVerifier `
        --leveled-plugin (Join-Path $negativeControlRoot "CreatedList.esp") `
        --leveled-proposal (Join-Path $negativeControlRoot `
            "CreatedList.leveled-list-production-proposal.json") `
        --outfit-plugin (Join-Path $negativeControlRoot "GeneratedListOutfit.esp") `
        --outfit-proposal (Join-Path $negativeControlRoot `
            "generated-list.outfit-proposal.json") `
        --report $negativeRawReport 2>&1) -join "`n"
    if ($LASTEXITCODE -eq 0) {
        throw "The SKY-GUI-017 raw verifier accepted the one-row SKY-GUI-016 control: $negativeText"
    }
    $negativeAudit = Get-Content -LiteralPath $negativeRawReport -Raw |
        ConvertFrom-Json
    if ($negativeAudit.passed -or $negativeAudit.failures.Count -lt 2) {
        throw "The negative raw audit did not expose both binary and proposal row mismatches."
    }
    $checks.verifierRejectsOneRowControl = $true

    $application = Start-Process -FilePath $executable -PassThru
    $script:mainHandle = [IntPtr]::Zero
    Wait-Until -Failure "The packaged desktop did not create a usable main window." `
        -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui017Native]::VisibleWindowsForProcess(
                [uint32]$application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            $rect = New-Object SkyGui017Native+Rect
            if ([SkyGui017Native]::GetWindowRect($candidate, [ref]$rect) -and
                ($rect.Right - $rect.Left) -ge 800 -and
                ($rect.Bottom - $rect.Top) -ge 600) {
                $script:mainHandle = $candidate
                return $true
            }
        }
        return $false
    }
    [void]$application.WaitForInputIdle(10000)
    [void][SkyGui017Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui017Native]::SetForegroundWindow($mainHandle)
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
    Wait-Until -Failure "The leveled-list task did not enable after workspace review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Create Skyrim leveled lists task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Create Skyrim leveled lists task"
    Set-EditValue $mainRoot "Leveled-list catalog seed" "17"
    Set-EditValue $mainRoot "New leveled-list local FormID" "0x00000A10"
    Set-EditValue $mainRoot "New leveled-list output plugin" $leveledPlugin
    Set-EditValue $mainRoot "Leveled-list production proposal path" $leveledProposal
    Invoke-Button $mainRoot "Load reviewed leveled-list entry catalog"
    Wait-Until -Failure "The reviewed LVLI entry catalog did not load." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open leveled-list header and entry editors").Current.IsEnabled -and
            (Test-TextContains $mainRoot "2 ARMO and 1 LVLI")
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.reviewedCatalogLoad = $true

    $escapeHeader = Open-LeveledHeaderModal $application $mainHandle $mainRoot
    Set-EditValue $escapeHeader.Root "Leveled-list name suffix" "EscapeDiscarded"
    [void][SkyGui017Native]::SetForegroundWindow($escapeHeader.Handle)
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Wait-ModalClosed $application $escapeHeader.Handle $escapeHeader.Helper
    Wait-Until -Failure "Escape leaked an LVLI document." -Condition {
        Test-TextContains $mainRoot "No accepted header and LVLO row"
    }
    $checks.escapeRollback = $true

    $closeHeader = Open-LeveledHeaderModal $application $mainHandle $mainRoot
    Set-EditValue $closeHeader.Root "Leveled-list name suffix" "CloseDiscarded"
    [void][SkyGui017Native]::PostMessage(
        $closeHeader.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeHeader.Handle $closeHeader.Helper
    Wait-Until -Failure "Title close leaked an LVLI document." -Condition {
        Test-TextContains $mainRoot "No accepted header and LVLO row"
    }
    $checks.titleBarCloseRollback = $true

    $header = Open-LeveledHeaderModal $application $mainHandle $mainRoot
    Set-EditValue $header.Root "Leveled-list name suffix" "TravelGear"
    Set-ToggleOn $header.Root "Calculate from all eligible levels flag"
    Set-ToggleOn $header.Root "Use all entries flag"
    Set-RangeValue $header.Root `
        "Chance None percent from zero through one hundred" 25
    Set-RangeValue $header.Root `
        "Maximum count from zero through two hundred fifty five" 0
    $editorIdPreview = Find-Element $header.Root `
        ([System.Windows.Automation.ControlType]::Edit) `
        "Complete leveled-list EditorID"
    Wait-Until -Failure "The live LVLI EditorID or flags did not update." -Condition {
        ([System.Windows.Automation.ValuePattern]$editorIdPreview.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq
            "npcm_LVLI_TravelGear" -and
        (Test-TextContains $header.Root "LVLF 0x05")
    }
    Capture-Window $header.Handle $screenshots.header
    Press-Button $header.Root "Create leveled-list header" $header.Handle
    $entry = Wait-ProcessWindowByTitle $application $mainHandle `
        "Add leveled-list entry" "The Gate 017 entry editor did not become visible."
    Set-EditValue $entry.Root "Entry level from one through 32767" "7"
    Set-EditValue $entry.Root "Entry count from one through 32767" "2"
    Capture-Window $entry.Handle $screenshots.entry
    Press-Button $entry.Root "Apply leveled-entry values" $entry.Handle
    Wait-Until -Failure "The complete LVLI editor sequence did not close." `
        -TimeoutSeconds 45 -Condition { $header.Helper.HasExited }
    Wait-Until -Failure "The accepted LVLI did not reach the production parent." -Condition {
        (Test-TextContains $mainRoot "npcm_LVLI_TravelGear") -and
        (Test-TextContains $mainRoot "LVLF 0x05") -and
        (Test-TextContains $mainRoot "1 row")
    }
    $checks.initialOwnerCreation = $true

    $addBoundary = Open-ButtonModal $application $mainHandle $mainRoot `
        "Add leveled-list row" "Add leveled-list entry"
    $levelBox = Find-Element $addBoundary.Root `
        ([System.Windows.Automation.ControlType]::Edit) `
        "Entry level from one through 32767"
    Wait-Until -Failure "The Add editor did not focus Level on open." -Condition {
        $levelBox.Current.HasKeyboardFocus
    }
    Resize-Window $addBoundary.Handle 760 900
    Set-EditValue $addBoundary.Root "Entry level from one through 32767" "0"
    Wait-Until -Failure "Invalid Level did not disable Add or expose a live range error." `
        -Condition {
        -not (Test-ButtonEnabled $addBoundary.Root "Apply leveled-entry values") -and
            (Test-TextContains $addBoundary.Root "Level must be from 1 through 32767")
    }
    Capture-Window $addBoundary.Handle $screenshots.invalid
    Set-EditValue $addBoundary.Root "Entry level from one through 32767" "32767"
    Set-EditValue $addBoundary.Root "Entry count from one through 32767" "32767"
    Wait-Until -Failure "The maximum legal Skyrim LVLO boundary was not accepted." -Condition {
        Test-ButtonEnabled $addBoundary.Root "Apply leveled-entry values"
    }
    Capture-Window $addBoundary.Handle $screenshots.boundary
    $countBox = Find-Element $addBoundary.Root `
        ([System.Windows.Automation.ControlType]::Edit) `
        "Entry count from one through 32767"
    [void][SkyGui017Native]::SetForegroundWindow($addBoundary.Handle)
    $countBox.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $application $addBoundary.Handle $addBoundary.Helper
    Wait-Until -Failure "Enter did not append the maximum repeated-reference row." `
        -Condition {
        (Test-TextContains $mainRoot "2 row(s)") -and
            @(Get-ListItems $mainRoot `
                "Ordered authored leveled-list rows").Count -eq 2
    }
    $checks.addInvalidBoundaryEnterFocusAndScaling = $true

    Select-ListItemAt $mainRoot "Ordered authored leveled-list rows" 0
    $edit = Open-ButtonModal $application $mainHandle $mainRoot `
        "Edit selected leveled-list row" "Edit leveled-list entry"
    if ((Get-EditValue $edit.Root "Entry level from one through 32767") -ne "7" -or
        (Get-EditValue $edit.Root "Entry count from one through 32767") -ne "2") {
        throw "Edit did not seed the exact selected repeated-reference row."
    }
    Set-EditValue $edit.Root "Entry level from one through 32767" "1"
    Set-EditValue $edit.Root "Entry count from one through 32767" "1"
    Capture-Window $edit.Handle $screenshots.edit
    Press-Button $edit.Root "Apply leveled-entry values" $edit.Handle
    Wait-ModalClosed $application $edit.Handle $edit.Helper
    if (@(Get-ListItems $mainRoot "Ordered authored leveled-list rows").Count -ne 2) {
        throw "Edit changed the number of authored rows."
    }
    $checks.editReplacedOnlySelectedRow = $true

    $escapeEntry = Open-ButtonModal $application $mainHandle $mainRoot `
        "Add leveled-list row" "Add leveled-list entry"
    Set-EditValue $escapeEntry.Root "Entry level from one through 32767" "12"
    [void][SkyGui017Native]::SetForegroundWindow($escapeEntry.Handle)
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Wait-ModalClosed $application $escapeEntry.Handle $escapeEntry.Helper

    $cancelEntry = Open-ButtonModal $application $mainHandle $mainRoot `
        "Add leveled-list row" "Add leveled-list entry"
    Set-EditValue $cancelEntry.Root "Entry level from one through 32767" "13"
    Press-Button $cancelEntry.Root "Cancel leveled-entry edit" $cancelEntry.Handle
    Wait-ModalClosed $application $cancelEntry.Handle $cancelEntry.Helper

    Select-ListItemAt $mainRoot "Ordered authored leveled-list rows" 0
    $closeEntry = Open-ButtonModal $application $mainHandle $mainRoot `
        "Edit selected leveled-list row" "Edit leveled-list entry"
    Set-EditValue $closeEntry.Root "Entry level from one through 32767" "14"
    [void][SkyGui017Native]::PostMessage(
        $closeEntry.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeEntry.Handle $closeEntry.Helper
    if (@(Get-ListItems $mainRoot "Ordered authored leveled-list rows").Count -ne 2) {
        throw "Escape, Cancel, or title close changed the authored row count."
    }
    $checks.escapeCancelAndTitleCloseRollback = $true

    Select-ListItemAt $mainRoot "Ordered authored leveled-list rows" 1
    Invoke-Button $mainRoot "Remove selected leveled-list row"
    Wait-Until -Failure "Remove did not delete exactly the selected repeated row." -Condition {
        Test-TextContains $mainRoot "1 row"
    }
    if (@(Get-ListItems $mainRoot "Ordered authored leveled-list rows").Count -ne 1) {
        throw "Remove did not leave exactly the unaffected first row."
    }
    $readd = Open-ButtonModal $application $mainHandle $mainRoot `
        "Add leveled-list row" "Add leveled-list entry"
    Set-EditValue $readd.Root "Entry level from one through 32767" "32767"
    Set-EditValue $readd.Root "Entry count from one through 32767" "32767"
    Press-Button $readd.Root "Apply leveled-entry values" $readd.Handle
    Wait-ModalClosed $application $readd.Handle $readd.Helper
    Wait-Until -Failure "Re-add did not restore the exact final ordered rows." -Condition {
        (Test-TextContains $mainRoot "2 row(s)") -and
            @(Get-ListItems $mainRoot `
                "Ordered authored leveled-list rows").Count -eq 2
    }
    Capture-Window $mainHandle $screenshots.finalRows
    $checks.removeAndReaddPreservedOrder = $true

    if ((Test-Path -LiteralPath $leveledProposal) -or
        (Test-Path -LiteralPath $leveledPlugin)) {
        throw "The owner-modal editors wrote before explicit Review."
    }
    Invoke-Button $mainRoot "Review leveled-list proposal"
    Wait-Until -Failure "LVLI proposal review did not remain proposal-only." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $leveledProposal) -and
            -not (Test-Path -LiteralPath $leveledPlugin) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    $checks.reviewProposalOnly = $true
    Invoke-Button $mainRoot "Write and verify leveled-list output"
    Wait-Until -Failure "LVLI output was not retained after two readbacks." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $leveledPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verified
    $checks.writeAndSecondReadback = $true

    Copy-Item -LiteralPath $leveledPlugin `
        -Destination (Join-Path $phase2DataRoot "CreatedList.esp")
    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $phase2DataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $phase2LoadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $phase2PreflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The phase-two outfit task did not enable." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Browse and author Skyrim outfits task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Browse and author Skyrim outfits task"
    Set-EditValue $mainRoot "Outfit template plugin" "OutfitProviderAfter.esp"
    Set-EditValue $mainRoot "Outfit template FormID" "0x00000900"
    Set-EditValue $mainRoot "New outfit local FormID" "0x00000A20"
    Set-EditValue $mainRoot "Outfit initial leveled-list seed" "17"
    Set-EditValue $mainRoot "Outfit proposal path" $outfitProposal
    Set-EditValue $mainRoot "Outfit output plugin" $outfitPlugin
    Invoke-Button $mainRoot "Load reviewed outfit catalog"
    Wait-Until -Failure "The phase-two outfit catalog did not load." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open Skyrim outfit workbench").Current.IsEnabled -and
            (Test-TextContains $mainRoot "2 ARMO / 2 LVLI")
    }
    $cancelledOutfit = Open-OutfitModal $application $mainHandle $mainRoot
    Press-Button $cancelledOutfit.Root "Begin a new outfit" $cancelledOutfit.Handle
    Set-EditValue $cancelledOutfit.Root "Filter armor and leveled-list items" `
        "npcm_LVLI_TravelGear"
    Select-ItemContaining $cancelledOutfit.Root "Available ARMO and LVLI items" `
        "npcm_LVLI_TravelGear"
    Invoke-Button $cancelledOutfit.Root "Add selected item to outfit"
    Capture-Window $cancelledOutfit.Handle $screenshots.outfitCancelled
    Press-Button $cancelledOutfit.Root `
        "Cancel the whole outfit transaction" $cancelledOutfit.Handle
    Wait-ModalClosed $application $cancelledOutfit.Handle $cancelledOutfit.Helper
    Wait-Until -Failure "Cancelled outer outfit transaction leaked accepted state." -Condition {
        Test-TextContains $mainRoot "No accepted outfit choice or authored transaction"
    }
    if ((Test-Path -LiteralPath $outfitProposal) -or
        (Test-Path -LiteralPath $outfitPlugin)) {
        throw "Cancelled outer outfit transaction produced a proposal or plugin."
    }
    $checks.cancelledOuterOutfitProducedNoArtifacts = $true

    $outfit = Open-OutfitModal $application $mainHandle $mainRoot
    Press-Button $outfit.Root "Begin a new outfit" $outfit.Handle
    Set-EditValue $outfit.Root "Filter armor and leveled-list items" `
        "npcm_LVLI_TravelGear"
    Select-ItemContaining $outfit.Root "Available ARMO and LVLI items" `
        "npcm_LVLI_TravelGear"
    Invoke-Button $outfit.Root "Add selected item to outfit"
    Capture-Window $outfit.Handle $screenshots.outfit
    Press-Button $outfit.Root `
        "Use the selected outfit or save the authored proposal" $outfit.Handle
    Wait-ModalClosed $application $outfit.Handle $outfit.Helper
    Wait-Until -Failure "The generated LVLI did not enter the real outfit proposal." -Condition {
        Test-TextContains $mainRoot "New OTFT staged with 1 ordered item"
    }
    Invoke-Button $mainRoot "Review outfit proposal"
    Wait-Until -Failure "Generated-LVLI outfit review did not remain proposal-only." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $outfitProposal) -and
            -not (Test-Path -LiteralPath $outfitPlugin) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    Invoke-Button $mainRoot "Write and verify outfit output"
    Wait-Until -Failure "Generated-LVLI outfit output did not pass readback." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $outfitPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    $checks.generatedLvliUsedInRealOutfit = $true

    $rawText = (& python $rawVerifier `
        --leveled-plugin $leveledPlugin `
        --leveled-proposal $leveledProposal `
        --outfit-plugin $outfitPlugin `
        --outfit-proposal $outfitProposal `
        --report $rawReport 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent raw LVLI/outfit verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed) {
        throw "Independent raw audit did not prove the exact LVLI and follow-on OTFT."
    }
    $checks.independentRawLvliAndOutfitAudit = $true

    $application.CloseMainWindow() | Out-Null
    if (-not $application.WaitForExit(15000)) {
        throw "The packaged desktop did not close after acceptance."
    }
    $checks.cleanProcessExit = $true

    $publishFiles = @(Get-ChildItem -LiteralPath $publishRootFull -Recurse -File)
    $report = [ordered]@{
        schema = "npcmanager.sky-gui-017.packaged-acceptance.v1"
        runTag = $RunTag
        passed = @($checks.Values | Where-Object { -not $_ }).Count -eq 0
        checks = $checks
        publish = [ordered]@{
            path = Get-RelativeLabPath $publishRootFull
            fileCount = $publishFiles.Count
            executableSha256 = (Get-FileHash -LiteralPath $executable `
                -Algorithm SHA256).Hash
            desktopDllSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $publishRootFull "NpcManager.Desktop.dll") `
                -Algorithm SHA256).Hash
        }
        fixture = [ordered]@{
            phase1SourceBase = Get-RelativeLabPath (Join-Path $dataRoot "OutfitBase.esm")
            phase1SourceBaseSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $dataRoot "OutfitBase.esm") -Algorithm SHA256).Hash
            phase1SourceProvider = Get-RelativeLabPath `
                (Join-Path $dataRoot "OutfitProvider.esp")
            phase1SourceProviderSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $dataRoot "OutfitProvider.esp") -Algorithm SHA256).Hash
            phase2SourceProvider = Get-RelativeLabPath $sourceProvider
            phase2SourceProviderSha256 = (Get-FileHash -LiteralPath $sourceProvider `
                -Algorithm SHA256).Hash
            catalogSeed = 17
            targetFormId = "0x00000A10"
            editorId = "npcm_LVLI_TravelGear"
            flags = "0x05"
            chanceNone = 25
            orderedEntries = @(
                [ordered]@{
                    reference = "OutfitBase.esm|0x00000800"
                    level = 1
                    count = 1
                    chanceNone = 0
                },
                [ordered]@{
                    reference = "OutfitBase.esm|0x00000800"
                    level = 32767
                    count = 32767
                    chanceNone = 0
                }
            )
        }
        outputs = [ordered]@{
            leveledProposal = Get-RelativeLabPath $leveledProposal
            leveledProposalSha256 = (Get-FileHash -LiteralPath $leveledProposal `
                -Algorithm SHA256).Hash
            leveledPlugin = Get-RelativeLabPath $leveledPlugin
            leveledPluginSha256 = (Get-FileHash -LiteralPath $leveledPlugin `
                -Algorithm SHA256).Hash
            outfitProposal = Get-RelativeLabPath $outfitProposal
            outfitProposalSha256 = (Get-FileHash -LiteralPath $outfitProposal `
                -Algorithm SHA256).Hash
            outfitPlugin = Get-RelativeLabPath $outfitPlugin
            outfitPluginSha256 = (Get-FileHash -LiteralPath $outfitPlugin `
                -Algorithm SHA256).Hash
            rawAudit = Get-RelativeLabPath $rawReport
            negativeControlAudit = Get-RelativeLabPath $negativeRawReport
        }
        screenshots = [ordered]@{}
        currentNpcRuntimeScreenshot = [ordered]@{
            path = Get-RelativeLabPath $currentNpcScreenshot
            sha256 = (Get-FileHash -LiteralPath $currentNpcScreenshot `
                -Algorithm SHA256).Hash
            scope = "User-supplied gross-render evidence only; not outfit or provider authority."
        }
        pluginAuthority = $true
        previewAuthority = $false
        equipmentAuthority = $false
        runtimeAuthority = $false
        visualAuthority = $false
        verdict = "STATIC_PASS_RUNTIME_REQUIRED"
    }
    foreach ($entry in $screenshots.GetEnumerator()) {
        $report.screenshots[$entry.Key] = [ordered]@{
            path = Get-RelativeLabPath $entry.Value
            sha256 = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        }
    }
    $json = $report | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText(
        $reportPathFull, $json + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output $json
}
finally {
    foreach ($helper in $activeHelpers) {
        if (-not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        Stop-Process -Id $application.Id -Force -ErrorAction SilentlyContinue
    }
}
