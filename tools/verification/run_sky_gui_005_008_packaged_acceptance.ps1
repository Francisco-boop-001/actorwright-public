param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260722-1"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot "03-builds\work\sky-gui-004-desktop-publish-20260722-41"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$accepted004Input = Join-Path $projectRoot "03-builds\work\sky-gui-004-acceptance-input-20260722-1"
$dataRoot = Join-Path $accepted004Input "Data"
$loadOrderPath = Join-Path $accepted004Input "load-order.json"
$sourcePackage = Join-Path $projectRoot "03-builds\work\gui-preset-npc-20260722-211824-061-1439fdf242d7"
$sourcePlugin = Join-Path $sourcePackage "Data\Gate2Emi2Generated.esp"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-005-008-acceptance-input-$RunTag"
$sourceData = Join-Path $inputRoot "SourceData"
$copiedSource = Join-Path $sourceData "Gate2Emi2Generated.esp"
$preflightOutput = Join-Path $inputRoot "future-preflight-output"
$packageRoot = Join-Path $projectRoot "03-builds\work\sky-gui-005-008-gui-package-$RunTag"
$outputPluginName = "Gate2Emi2Generated-GuiVisible.esp"
$outputPlugin = Join-Path $packageRoot "Data\$outputPluginName"
$sourceHash = (Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash
$screenshots = [ordered]@{
    identityCancel = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-identity-cancel-$RunTag.png"
    identityAccepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-identity-accepted-$RunTag.png"
    statisticsTitleClose = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-statistics-title-close-$RunTag.png"
    statisticsAccepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-statistics-accepted-$RunTag.png"
    collectionsCancel = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-collections-cancel-$RunTag.png"
    factionAccepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-006-faction-accepted-$RunTag.png"
    inventoryAccepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-007-inventory-accepted-$RunTag.png"
    outfitVisible = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-outfit-visible-$RunTag.png"
    perkAccepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-008-perk-accepted-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-reviewed-$RunTag.png"
    verifiedSummary = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-verified-summary-$RunTag.png"
    verifiedOutput = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-005-verified-output-$RunTag.png"
}

foreach ($path in @($executable, $dataRoot, $loadOrderPath, $sourcePlugin)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required acceptance input is absent: $path" }
}
foreach ($path in @($inputRoot, $packageRoot)) {
    if (Test-Path -LiteralPath $path) { throw "Acceptance output exists and will not be overwritten: $path" }
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) { throw "Screenshot evidence exists and will not be overwritten: $path" }
}

function Get-RelativeLabPath {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($labRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not below the lab root: $full"
    }
    return $full.Substring($labRoot.Length + 1).Replace("\", "/")
}

[void](New-Item -ItemType Directory -Path $sourceData)
Copy-Item -LiteralPath $sourcePlugin -Destination $copiedSource
foreach ($plugin in @(
    "Skyrim.esm",
    "High Poly Head.esm",
    "Improved Eyes Skyrim.esp",
    "Koralina's Eyebrows.esp",
    "KS Hairdo's.esp",
    "GoamElvenEars.esp")) {
    $provider = Join-Path $dataRoot $plugin
    if (-not (Test-Path -LiteralPath $provider -PathType Leaf)) {
        throw "Required copied provider is absent: $provider"
    }
    Copy-Item -LiteralPath $provider -Destination (Join-Path $sourceData $plugin)
}
if ((Get-FileHash -LiteralPath $copiedSource -Algorithm SHA256).Hash -ne $sourceHash) {
    throw "The copied source plugin does not match the accepted SKY-GUI-004 output."
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

public static class SkyGui005Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

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
    param([scriptblock]$Condition, [string]$Failure, [int]$TimeoutSeconds = 25)
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
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
}

function Find-Element {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ElementCondition $ControlType $Name))
    if ($null -eq $element) { throw "Required $($ControlType.ProgrammaticName) '$Name' was not found." }
    return $element
}

function Try-FindElement {
    param([System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType, [string]$Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ElementCondition $ControlType $Name))
}

function Find-ByAutomationId {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$AutomationId)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Required AutomationId '$AutomationId' was not found." }
    return $element
}

function Invoke-Button {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $script:buttonToInvoke = $null
    Wait-Until -Failure "Required button '$Name' did not finish rendering." -Condition {
        $script:buttonToInvoke = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::Button) $Name
        $null -ne $script:buttonToInvoke
    }
    $button = $script:buttonToInvoke
    if (-not $button.Current.IsEnabled) { throw "Required button '$Name' is disabled." }
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Select-Tab {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $script:tabToSelect = $null
    Wait-Until -Failure "Tab '$Name' did not finish rendering." -Condition {
        $script:tabToSelect = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::TabItem) $Name
        $null -ne $script:tabToSelect
    }
    $tab = $script:tabToSelect
    $selection = [System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    Wait-Until -Failure "Tab '$Name' did not become selected." -Condition {
        ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    }
    Start-Sleep -Milliseconds 160
}

function Set-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [string]$Value)
    $script:editToSet = $null
    Wait-Until -Failure "Edit '$Name' did not finish rendering." -Condition {
        $script:editToSet = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::Edit) $Name
        $null -ne $script:editToSet
    }
    $edit = $script:editToSet
    $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    if ($pattern.Current.IsReadOnly) { throw "Edit '$Name' is read-only." }
    $pattern.SetValue($Value)
    Wait-Until -Failure "Edit '$Name' did not retain '$Value'." -Condition {
        ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $Value
    }
}

function Get-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $script:editToRead = $null
    Wait-Until -Failure "Edit '$Name' did not finish rendering for readback." -Condition {
        $script:editToRead = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::Edit) $Name
        $null -ne $script:editToRead
    }
    $edit = $script:editToRead
    return ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}

function Set-Checked {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [bool]$Checked)
    $box = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $toggle = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $desired = if ($Checked) { [System.Windows.Automation.ToggleState]::On } else {
        [System.Windows.Automation.ToggleState]::Off
    }
    if ($toggle.Current.ToggleState -ne $desired) { $toggle.Toggle() }
    Wait-Until -Failure "Checkbox '$Name' did not reach $desired." -Condition {
        ([System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)).Current.ToggleState -eq $desired
    }
}

function Set-ComboValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name, [string]$Value)
    $script:comboToSet = $null
    Wait-Until -Failure "Combo '$Name' did not finish rendering." -Condition {
        $script:comboToSet = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::ComboBox) $Name
        $null -ne $script:comboToSet
    }
    $combo = $script:comboToSet
    $desiredIndex = switch ($Value) {
        "Fixed" { 0 }
        "Multiplier" { 1 }
        "Keep" { 0 }
        "Set" { 1 }
        "Clear" { 2 }
        default { throw "Combo '$Name' has no bounded automation index for '$Value'." }
    }
    if ((Get-ComboValue $Root $Name) -eq $Value) { return }
    $selectedByPattern = $false
    try {
        $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        $condition = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::ListItem)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $Value)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $false)))
        $script:selectableComboItem = $null
        Wait-Until -Failure "Combo '$Name' did not expose selectable item '$Value'." `
            -TimeoutSeconds 5 -Condition {
            $script:selectableComboItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants, $condition)
            $null -ne $script:selectableComboItem
        }
        ([System.Windows.Automation.SelectionItemPattern]$script:selectableComboItem.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        try { $expand.Collapse() } catch { }
        $selectedByPattern = $true
    }
    catch {
        try {
            ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
                [System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        } catch { }
    }
    if (-not $selectedByPattern) {
        $currentIndex = switch (Get-ComboValue $Root $Name) {
            "Fixed" { 0 }
            "Multiplier" { 1 }
            "Keep" { 0 }
            "Set" { 1 }
            "Clear" { 2 }
            default { -1 }
        }
        [void][SkyGui005Native]::SetForegroundWindow([IntPtr]$Root.Current.NativeWindowHandle)
        $combo.SetFocus()
        if ($currentIndex -lt 0) {
            [System.Windows.Forms.SendKeys]::SendWait("{HOME}")
            $currentIndex = 0
        }
        while ($currentIndex -lt $desiredIndex) {
            [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
            $currentIndex++
        }
        while ($currentIndex -gt $desiredIndex) {
            [System.Windows.Forms.SendKeys]::SendWait("{UP}")
            $currentIndex--
        }
        [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    }
    try {
        Wait-Until -Failure "Combo '$Name' did not select '$Value'." -Condition {
            (Get-ComboValue $Root $Name) -eq $Value
        }
    }
    catch {
        $observed = Get-ComboValue $Root $Name
        throw "$($_.Exception.Message) Observed selection: '$observed'."
    }
}

function Get-ComboValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $combo = Try-FindElement $Root ([System.Windows.Automation.ControlType]::ComboBox) $Name
    if ($null -eq $combo) { return "" }
    $selection = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    $selected = $selection.Current.GetSelection()
    return $(if ($selected.Count -eq 1) { $selected[0].Current.Name } else { "" })
}

function Test-TextContains {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Needle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($item in $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($item.Current.Name.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

function Get-TextSnapshot {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    return (($Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        ForEach-Object { $_.Current.Name } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique) -join " || ")
}

function Get-GridRowCount {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $grid = Find-Element $Root ([System.Windows.Automation.ControlType]::DataGrid) $Name
    return ([System.Windows.Automation.GridPattern]$grid.GetCurrentPattern(
        [System.Windows.Automation.GridPattern]::Pattern)).Current.RowCount
}

function Set-GridCellValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$GridName,
        [int]$Row, [int]$Column, [string]$Value)
    $grid = Find-Element $Root ([System.Windows.Automation.ControlType]::DataGrid) $GridName
    $gridPattern = [System.Windows.Automation.GridPattern]$grid.GetCurrentPattern(
        [System.Windows.Automation.GridPattern]::Pattern)
    $cell = $gridPattern.GetItem($Row, $Column)
    [void][SkyGui005Native]::SetForegroundWindow([IntPtr]$Root.Current.NativeWindowHandle)
    $cell.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{F2}")
    $script:cellEdit = $null
    $editCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    Wait-Until -Failure "Grid '$GridName' row $Row column $Column did not enter edit mode." -Condition {
        $script:cellEdit = $cell.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $editCondition)
        $null -ne $script:cellEdit
    }
    $valuePattern = [System.Windows.Automation.ValuePattern]$script:cellEdit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $valuePattern.SetValue($Value)
    [System.Windows.Forms.SendKeys]::SendWait("{TAB}")
    Start-Sleep -Milliseconds 180
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    try {
        $window = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)
        $window.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    } catch { }
    $rect = New-Object SkyGui005Native+Rect
    $restored = $false
    foreach ($ignored in 1..20) {
        [void][SkyGui005Native]::ShowWindow($Handle, 9)
        [void][SkyGui005Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui005Native]::PostMessage($Handle, 0x0112, [IntPtr]0xF120, [IntPtr]::Zero)
        [void][SkyGui005Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui005Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui005Native]::IsIconic($Handle) -and
            ($rect.Right - $rect.Left) -ge 640 -and ($rect.Bottom - $rect.Top) -ge 400) {
            $restored = $true
            break
        }
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if (-not $restored) { throw "Screenshot target did not restore ($width x $height)." }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            $screenContext = [SkyGui005Native]::GetDC([IntPtr]::Zero)
            try {
                if ($screenContext -eq [IntPtr]::Zero -or
                    -not [SkyGui005Native]::BitBlt($deviceContext, 0, 0, $width, $height,
                        $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                    if (-not [SkyGui005Native]::PrintWindow($Handle, $deviceContext, 0)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            } finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui005Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
                }
                $graphics.ReleaseHdc($deviceContext)
            }
        } finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Start-OwnedModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [string]$ButtonName, [string]$WindowTitle = "", [int]$TimeoutSeconds = 180)
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$($MainHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '$ButtonName')))
`$button = `$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) { throw 'Modal opener absent or disabled.' }
([System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) -WindowStyle Hidden -PassThru
    try {
        $script:modalHandle = [IntPtr]::Zero
        Wait-Until -Failure "Owned modal '$WindowTitle' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            foreach ($row in [SkyGui005Native]::VisibleWindowsForProcess([uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $MainHandle -and
                    ([string]::IsNullOrEmpty($WindowTitle) -or $parts[1] -eq $WindowTitle)) {
                    $script:modalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        return [pscustomobject]@{
            Handle = $script:modalHandle
            Root = [System.Windows.Automation.AutomationElement]::FromHandle($script:modalHandle)
            Helper = $helper
        }
    } catch {
        if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
        throw
    }
}

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper, [int]$TimeoutSeconds = 45)
    Wait-Until -Failure "Owned modal did not close." -TimeoutSeconds $TimeoutSeconds -Condition {
        -not ([SkyGui005Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) { throw "The modal opener remained blocked after close." }
}

function Choose-SourcePlugin {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [System.Windows.Automation.AutomationElement]$MainRoot, [string]$Path)
    $session = Start-OwnedModal $Application $MainHandle "Browse existing NPC source plugin" "" 45
    try {
        [void][SkyGui005Native]::SetForegroundWindow($session.Handle)
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait("%n")
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        [System.Windows.Forms.SendKeys]::SendWait($Path)
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        $closed = $false
        try {
            Wait-Until -Failure "Filename accelerator did not close the native picker." `
                -TimeoutSeconds 6 -Condition {
                -not ([SkyGui005Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
                    Where-Object { $_.StartsWith($session.Handle.ToInt64().ToString() + "|") })
            }
            $closed = $true
        }
        catch { }
        if (-not $closed) {
            [void][SkyGui005Native]::SetForegroundWindow($session.Handle)
            [System.Windows.Forms.SendKeys]::SendWait("^l")
            [System.Windows.Forms.SendKeys]::SendWait("^a")
            [System.Windows.Forms.SendKeys]::SendWait($Path)
            [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
            Start-Sleep -Milliseconds 450
            [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        }
        Wait-ModalClosed $Application $session.Handle $session.Helper 45
    } catch {
        if (-not $session.Helper.HasExited) { Stop-Process -Id $session.Helper.Id -Force }
        throw
    }
    Wait-Until -Failure "The production picker did not hash-bind the selected source." `
        -TimeoutSeconds 60 -Condition {
        (Get-EditValue $MainRoot "Existing NPC source plugin") -eq $Path -and
            (Get-EditValue $MainRoot "Existing NPC source hash") -eq $sourceHash
    }
}

$application = $null
$activeHelpers = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $application = Start-Process -FilePath $executable -PassThru
    Wait-Until -Failure "The packaged desktop did not create a main window." `
        -TimeoutSeconds 30 -Condition { $application.Refresh(); $application.MainWindowHandle -ne 0 }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    [void][SkyGui005Native]::ShowWindow($mainHandle, 9)
    [void][SkyGui005Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Wait-Until -Failure "The packaged task shell did not render." -Condition {
        $null -ne (Try-FindElement $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Open copied Skyrim workspace task")
    }
    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Wait-Until -Failure "The copied workspace review did not enable." -TimeoutSeconds 60 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Review copied Skyrim workspace").Current.IsEnabled
    }
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The existing-NPC task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Edit existing NPC task").Current.IsEnabled
    }
    Select-Tab $mainRoot "Edit existing NPC task"
    Wait-Until -Failure "The existing-NPC panel did not render." -Condition {
        $null -ne (Try-FindElement $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Browse existing NPC source plugin")
    }

    Choose-SourcePlugin $application $mainHandle $mainRoot $copiedSource
    Set-EditValue $mainRoot "Existing NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Existing NPC output plugin filename" $outputPluginName
    Set-EditValue $mainRoot "Existing NPC output folder" $packageRoot
    Invoke-Button $mainRoot "Load existing NPC fields"
    try {
        Wait-Until -Failure "The exact source NPC did not load." -TimeoutSeconds 90 -Condition {
            (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
                "Edit existing NPC identity and archetype").Current.IsEnabled -and
            (Test-TextContains $mainRoot "Emi")
        }
    }
    catch {
        $snapshot = Get-TextSnapshot $mainRoot
        $sourceValue = Get-EditValue $mainRoot "Existing NPC source plugin"
        $hashValue = Get-EditValue $mainRoot "Existing NPC source hash"
        throw "$($_.Exception.Message) Source='$sourceValue' hash='$hashValue'. UI snapshot: $snapshot"
    }
    $checks.hashBoundSourceLoaded = $true

    $identityCancel = Start-OwnedModal $application $mainHandle `
        "Edit existing NPC identity and archetype" "Edit NPC identity and archetype" 180
    $activeHelpers.Add($identityCancel.Helper)
    Set-EditValue $identityCancel.Root "NPC EditorID" "CancelledIdentityProbe"
    Set-EditValue $identityCancel.Root "NPC full display name" "Cancelled identity probe"
    Capture-Window $identityCancel.Handle $screenshots.identityCancel
    Invoke-Button $identityCancel.Root "Cancel"
    Wait-ModalClosed $application $identityCancel.Handle $identityCancel.Helper

    $identityAccepted = Start-OwnedModal $application $mainHandle `
        "Edit existing NPC identity and archetype" "Edit NPC identity and archetype" 180
    $activeHelpers.Add($identityAccepted.Helper)
    if ((Get-EditValue $identityAccepted.Root "NPC EditorID") -ne "Gate2Emi2Generated" -or
        (Get-EditValue $identityAccepted.Root "NPC full display name") -ne "Emi") {
        throw "Identity Cancel did not restore the source-bound values."
    }
    Set-EditValue $identityAccepted.Root "NPC EditorID" "Gate2Emi2GeneratedGuiVisible"
    Set-EditValue $identityAccepted.Root "NPC full display name" "Emi GUI Visible NPC"
    Set-EditValue $identityAccepted.Root "NPC short name" "EmiVisible"
    Capture-Window $identityAccepted.Handle $screenshots.identityAccepted
    Invoke-Button $identityAccepted.Root "Save identity"
    Wait-ModalClosed $application $identityAccepted.Handle $identityAccepted.Helper
    $checks.identityCancelRollback = $true

    $statsClose = Start-OwnedModal $application $mainHandle `
        "Edit complete existing NPC statistics" "Edit Skyrim NPC statistics"
    $activeHelpers.Add($statsClose.Helper)
    Set-ComboValue $statsClose.Root "NPC level mode" "Fixed"
    Set-EditValue $statsClose.Root "NPC level value" "33"
    Capture-Window $statsClose.Handle $screenshots.statisticsTitleClose
    [void][SkyGui005Native]::PostMessage($statsClose.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $statsClose.Handle $statsClose.Helper

    $statsAccepted = Start-OwnedModal $application $mainHandle `
        "Edit complete existing NPC statistics" "Edit Skyrim NPC statistics"
    $activeHelpers.Add($statsAccepted.Helper)
    if ((Get-EditValue $statsAccepted.Root "NPC level value") -eq "33" -or
        (Get-ComboValue $statsAccepted.Root "NPC level mode") -ne "Multiplier") {
        throw "Statistics title-close did not roll back its working copy."
    }
    Set-ComboValue $statsAccepted.Root "NPC level mode" "Fixed"
    Set-EditValue $statsAccepted.Root "NPC level value" "19"
    Set-EditValue $statsAccepted.Root "NPC health offset" "11"
    Select-Tab $statsAccepted.Root "Gameplay flags"
    Set-Checked $statsAccepted.Root "essential" $true
    Select-Tab $statsAccepted.Root "Core stats"
    Capture-Window $statsAccepted.Handle $screenshots.statisticsAccepted
    Invoke-Button $statsAccepted.Root "Save statistics"
    Wait-ModalClosed $application $statsAccepted.Handle $statsAccepted.Helper
    $checks.statisticsTitleCloseRollback = $true

    $collectionsCancel = Start-OwnedModal $application $mainHandle `
        "Edit existing NPC gameplay lists" "Edit NPC lists"
    $activeHelpers.Add($collectionsCancel.Helper)
    Invoke-Button $collectionsCancel.Root "Add keyword"
    Set-GridCellValue $collectionsCancel.Root "NPC keywords" 0 0 "Skyrim.esm|0x00013794"
    Capture-Window $collectionsCancel.Handle $screenshots.collectionsCancel
    Invoke-Button $collectionsCancel.Root "Cancel"
    Wait-ModalClosed $application $collectionsCancel.Handle $collectionsCancel.Helper

    $collectionsAccepted = Start-OwnedModal $application $mainHandle `
        "Edit existing NPC gameplay lists" "Edit NPC lists"
    $activeHelpers.Add($collectionsAccepted.Helper)
    if ((Get-GridRowCount $collectionsAccepted.Root "NPC keywords") -ne 0) {
        throw "Collection Cancel retained a discarded keyword."
    }
    Invoke-Button $collectionsAccepted.Root "Add keyword"
    Set-GridCellValue $collectionsAccepted.Root "NPC keywords" 0 0 "Skyrim.esm|0x00013794"

    Select-Tab $collectionsAccepted.Root "Factions"
    $factionRow = Get-GridRowCount $collectionsAccepted.Root "NPC factions"
    Invoke-Button $collectionsAccepted.Root "Add faction"
    Set-GridCellValue $collectionsAccepted.Root "NPC factions" $factionRow 0 "Skyrim.esm|0x0005A1A4"
    Set-GridCellValue $collectionsAccepted.Root "NPC factions" $factionRow 1 "-2"
    Capture-Window $collectionsAccepted.Handle $screenshots.factionAccepted

    Select-Tab $collectionsAccepted.Root "Inventory"
    Invoke-Button $collectionsAccepted.Root "Add item"
    Set-GridCellValue $collectionsAccepted.Root "NPC inventory" 0 0 "Skyrim.esm|0x0000000F"
    Set-GridCellValue $collectionsAccepted.Root "NPC inventory" 0 1 "-7"
    Capture-Window $collectionsAccepted.Handle $screenshots.inventoryAccepted

    Select-Tab $collectionsAccepted.Root "Outfits"
    Capture-Window $collectionsAccepted.Handle $screenshots.outfitVisible

    Select-Tab $collectionsAccepted.Root "Perks"
    Invoke-Button $collectionsAccepted.Root "Add perk"
    Set-GridCellValue $collectionsAccepted.Root "NPC perks" 0 0 "Skyrim.esm|0x00058F6A"
    Set-GridCellValue $collectionsAccepted.Root "NPC perks" 0 1 "2"
    Capture-Window $collectionsAccepted.Handle $screenshots.perkAccepted

    Select-Tab $collectionsAccepted.Root "Actor effects"
    Invoke-Button $collectionsAccepted.Root "Add actor effect"
    Set-GridCellValue $collectionsAccepted.Root "NPC actor effects" 0 0 "Skyrim.esm|0x00012FCD"
    Invoke-Button $collectionsAccepted.Root "Save list changes"
    Wait-ModalClosed $application $collectionsAccepted.Handle $collectionsAccepted.Helper
    $checks.collectionsCancelRollback = $true

    if (Test-Path -LiteralPath $packageRoot) { throw "The UI wrote output before review." }
    Invoke-Button $mainRoot "Review existing NPC changes"
    Wait-Until -Failure "The exact change review did not become current." `
        -TimeoutSeconds 90 -Condition { Test-TextContains $mainRoot "Ready to create" }
    foreach ($field in @("EditorID", "Name", "ShortName", "Level", "Factions", "Inventory", "Perks")) {
        if (-not (Test-TextContains $mainRoot $field)) { throw "Reviewed field '$field' is not visible." }
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.noWriteBeforeReview = -not (Test-Path -LiteralPath $packageRoot)

    Invoke-Button $mainRoot "Create verified existing NPC override"
    Wait-Until -Failure "The verified existing-NPC package was not retained." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath (Join-Path $packageRoot "npcmanager-package.json")) -and
            (Test-Path -LiteralPath $outputPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verifiedSummary
    [void][SkyGui005Native]::SetForegroundWindow($mainHandle)
    [System.Windows.Forms.SendKeys]::SendWait("^{END}")
    Start-Sleep -Milliseconds 350
    Capture-Window $mainHandle $screenshots.verifiedOutput
    $checks.productionWriteCompleted = $true

    $manifest = Join-Path $packageRoot "npcmanager-package.json"
    $proposal = Join-Path $packageRoot "evidence\npc-edit-proposal.json"
    $verificationPath = Join-Path $packageRoot "evidence\npc-edit-verification.json"
    $dotnet = Join-Path $labRoot "tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe"
    $cli = Join-Path $projectRoot "src\NpcManager.Cli\bin\Release\net10.0\npcm.dll"
    $verifyText = (& $dotnet $cli package verify --manifest $manifest --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Independent package verification failed: $verifyText" }
    $packageVerification = $verifyText | ConvertFrom-Json
    if (-not $packageVerification.verified) { throw "Package verifier did not report verified=true." }
    $pluginText = (& $dotnet $cli plugin verify --edition skyrimse --before $copiedSource `
        --after $outputPlugin --proposal $proposal --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Independent plugin verification failed: $pluginText" }
    $pluginVerification = $pluginText | ConvertFrom-Json
    if (-not $pluginVerification.isValid) { throw "Plugin verifier did not report isValid=true." }
    $rawVerification = Get-Content -Raw -LiteralPath $verificationPath | ConvertFrom-Json
    if (-not $rawVerification.valid -or -not $rawVerification.trueOverride -or
        $rawVerification.majorRecordCount -ne 1 -or $rawVerification.npcRecordCount -ne 1 -or
        $rawVerification.sourceOwnedTargetCount -ne 1 -or $rawVerification.selfOwnedTargetCount -ne 0) {
        throw "Raw readback did not prove exactly one source-owned NPC override."
    }
    $observed = @($rawVerification.observedChanges | ForEach-Object { $_.field })
    foreach ($field in @("EditorID", "Name", "ShortName", "Level", "HealthOffset",
        "Keywords", "Factions", "Inventory", "Perks", "ActorEffects")) {
        if ($field -notin $observed) { throw "Raw readback omitted '$field'." }
    }
    $faction = $rawVerification.observedChanges | Where-Object field -eq "Factions"
    $inventory = $rawVerification.observedChanges | Where-Object field -eq "Inventory"
    $perk = $rawVerification.observedChanges | Where-Object field -eq "Perks"
    if ($faction.after -notmatch [regex]::Escape("Skyrim.esm|0x0005A1A4=-2") -or
        $inventory.after -ne "Skyrim.esm|0x0000000F=-7" -or
        $perk.after -ne "Skyrim.esm|0x00058F6A=2") {
        throw "Signed faction/inventory or unsigned perk readback drifted."
    }
    $checks.independentPackageVerification = $true
    $checks.independentPluginReadback = $true

    $screenshotEvidence = foreach ($entry in $screenshots.GetEnumerator()) {
        $item = Get-Item -LiteralPath $entry.Value
        [ordered]@{
            state = $entry.Key
            path = Get-RelativeLabPath $item.FullName
            size = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    [ordered]@{
        result = "PASS"
        surfaceIds = @("SKY-GUI-005", "SKY-GUI-006", "SKY-GUI-007", "SKY-GUI-008")
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        source = [ordered]@{
            plugin = Get-RelativeLabPath $copiedSource
            sha256 = $sourceHash
            targetFormId = "0x00000800"
            renderedScreenshotContext = "Screenshots/2026-07-22-NpcStudioEmiTrial.png"
        }
        package = [ordered]@{
            root = Get-RelativeLabPath $packageRoot
            manifest = Get-RelativeLabPath $manifest
            manifestSha256 = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
            plugin = Get-RelativeLabPath $outputPlugin
            pluginSha256 = (Get-FileHash -LiteralPath $outputPlugin -Algorithm SHA256).Hash
            packageVerified = [bool]$packageVerification.verified
            pluginVerified = [bool]$pluginVerification.isValid
            sourceOwnedTargetCount = $rawVerification.sourceOwnedTargetCount
            selfOwnedTargetCount = $rawVerification.selfOwnedTargetCount
            observedChanges = @($rawVerification.observedChanges)
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
        visualAuthority = $false
        protectedLiveRootTouched = $false
    } | ConvertTo-Json -Depth 14
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui005Native]::PostMessage([IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) { Stop-Process -Id $application.Id -Force }
    }
}
