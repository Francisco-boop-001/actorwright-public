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
        "03-builds\work\sky-gui-015-desktop-publish-20260723-1"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-015-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$fixtureRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-015-fixture-20260723-1"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-015-input-$RunTag"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$preflightOutput = Join-Path $inputRoot "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-015-output-$RunTag"
$newProposal = Join-Path $outputRoot "new.outfit-proposal.json"
$newPlugin = Join-Path $outputRoot "NewOutfitOutput.esp"
$overrideProposal = Join-Path $outputRoot "override.outfit-proposal.json"
$overridePlugin = Join-Path $outputRoot "OverrideOutfitOutput.esp"
$sourceProvider = Join-Path $dataRoot "OutfitProvider.esp"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_sky_gui_015_outfits.py"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-015-outfit-raw-audit-$RunTag.json"
$currentNpcScreenshot = Join-Path $labRoot "Resources\Screenshot\NpcStudioEmiTrial.png"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-loaded-$RunTag.png"
    browse = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-browse-$RunTag.png"
    newAuthor = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-new-author-$RunTag.png"
    newVerified = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-new-verified-$RunTag.png"
    overrideAuthor = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-override-author-$RunTag.png"
    overrideVerified = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-015-override-verified-$RunTag.png"
}

$required = @(
    $executable,
    (Join-Path $fixtureRoot "Data\OutfitBase.esm"),
    (Join-Path $fixtureRoot "Data\OutfitProvider.esp"),
    (Join-Path $fixtureRoot "load-order.json"),
    (Join-Path $fixtureRoot "expected.json"),
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
Copy-Item -LiteralPath (Join-Path $fixtureRoot "Data\OutfitBase.esm") `
    -Destination (Join-Path $dataRoot "OutfitBase.esm")
Copy-Item -LiteralPath (Join-Path $fixtureRoot "Data\OutfitProvider.esp") `
    -Destination $sourceProvider
Copy-Item -LiteralPath (Join-Path $fixtureRoot "load-order.json") `
    -Destination $loadOrderPath
$expected = Get-Content -LiteralPath (Join-Path $fixtureRoot "expected.json") `
    -Raw | ConvertFrom-Json
if ((Get-FileHash -LiteralPath (Join-Path $dataRoot "OutfitBase.esm") `
        -Algorithm SHA256).Hash -ne $expected.baseSha256.ToUpperInvariant() -or
    (Get-FileHash -LiteralPath $sourceProvider `
        -Algorithm SHA256).Hash -ne $expected.providerSha256.ToUpperInvariant()) {
    throw "Copied outfit fixture hashes do not match the frozen fixture contract."
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

public static class SkyGui015Native
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
    [void][SkyGui015Native]::ShowWindow($WindowHandle, 9)
    [void][SkyGui015Native]::SetForegroundWindow($WindowHandle)
    $button.SetFocus()
    Start-Sleep -Milliseconds 180
    $point = $button.GetClickablePoint()
    if (-not [SkyGui015Native]::SetCursorPos(
            [int][Math]::Round($point.X), [int][Math]::Round($point.Y))) {
        throw "Could not move to the clickable point for '$Name'."
    }
    [SkyGui015Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui015Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
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

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $rect = New-Object SkyGui015Native+Rect
    foreach ($ignored in 1..20) {
        [void][SkyGui015Native]::ShowWindow($Handle, 9)
        [void][SkyGui015Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui015Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui015Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui015Native]::IsIconic($Handle) -and
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
            $screenContext = [SkyGui015Native]::GetDC([IntPtr]::Zero)
            try {
                if (-not [SkyGui015Native]::PrintWindow(
                        $Handle, $deviceContext, 2)) {
                    if ($screenContext -eq [IntPtr]::Zero -or
                        -not [SkyGui015Native]::BitBlt(
                            $deviceContext, 0, 0, $width, $height,
                            $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui015Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
                }
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
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
        foreach ($row in [SkyGui015Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            if ($candidate -eq $MainHandle) { continue }
            $rect = New-Object SkyGui015Native+Rect
            if ([SkyGui015Native]::GetWindowRect($candidate, [ref]$rect) -and
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

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper)
    Wait-Until -Failure "The outfit workbench did not close." -TimeoutSeconds 45 `
        -Condition {
        -not ([SkyGui015Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
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
    $application = Start-Process -FilePath $executable -PassThru
    $script:mainHandle = [IntPtr]::Zero
    Wait-Until -Failure "The packaged desktop did not create a usable main window." `
        -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui015Native]::VisibleWindowsForProcess(
                [uint32]$application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            $rect = New-Object SkyGui015Native+Rect
            if ([SkyGui015Native]::GetWindowRect($candidate, [ref]$rect) -and
                ($rect.Right - $rect.Left) -ge 800 -and
                ($rect.Bottom - $rect.Top) -ge 600) {
                $script:mainHandle = $candidate
                return $true
            }
        }
        return $false
    }
    [void]$application.WaitForInputIdle(10000)
    [void][SkyGui015Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui015Native]::SetForegroundWindow($mainHandle)
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
    Wait-Until -Failure "The outfit task did not enable after workspace review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Browse and author Skyrim outfits task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Browse and author Skyrim outfits task"
    Set-EditValue $mainRoot "Outfit template plugin" "OutfitProvider.esp"
    Set-EditValue $mainRoot "Outfit template FormID" "0x00000900"
    Set-EditValue $mainRoot "New outfit local FormID" "0x00000A00"
    Set-EditValue $mainRoot "Outfit initial leveled-list seed" "11"
    Set-EditValue $mainRoot "Outfit proposal path" $newProposal
    Set-EditValue $mainRoot "Outfit output plugin" $newPlugin
    Invoke-Button $mainRoot "Load reviewed outfit catalog"
    Wait-Until -Failure "The reviewed outfit catalog did not load." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open Skyrim outfit workbench").Current.IsEnabled -and
            (Test-TextContains $mainRoot "2 ARMO / 1 LVLI")
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.reviewedCatalogLoad = $true

    $browseModal = Open-OutfitModal $application $mainHandle $mainRoot
    Select-ItemContaining $browseModal.Root "Outfit choices" "Record default"
    Select-ItemContaining $browseModal.Root "Outfit choices" "No outfit"
    Set-EditValue $browseModal.Root `
        "Filter outfits by name, plugin, FormID, or item" "ProductionBase"
    Select-ItemContaining $browseModal.Root "Outfit choices" "ProductionBaseOutfit"
    Capture-Window $browseModal.Handle $screenshots.browse
    [void][SkyGui015Native]::SetForegroundWindow($browseModal.Handle)
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Wait-ModalClosed $application $browseModal.Handle $browseModal.Helper
    Wait-Until -Failure "Escape leaked an accepted outfit state." -Condition {
        Test-TextContains $mainRoot "No accepted outfit choice"
    }
    $checks.browseFilterAndEscapeRollback = $true

    $closeModal = Open-OutfitModal $application $mainHandle $mainRoot
    Press-Button $closeModal.Root "Begin a new outfit" $closeModal.Handle
    [void][SkyGui015Native]::PostMessage(
        $closeModal.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeModal.Handle $closeModal.Helper
    Wait-Until -Failure "Title-bar close leaked an authored outfit state." -Condition {
        Test-TextContains $mainRoot "No accepted outfit choice"
    }
    $checks.titleBarCloseRollback = $true

    $newModal = Open-OutfitModal $application $mainHandle $mainRoot
    Press-Button $newModal.Root "Begin a new outfit" $newModal.Handle
    Set-EditValue $newModal.Root "Filter armor and leveled-list items" "Production armor A"
    Select-ItemContaining $newModal.Root "Available ARMO and LVLI items" "Production armor A"
    Invoke-Button $newModal.Root "Add selected item to outfit"
    Set-EditValue $newModal.Root "Filter armor and leveled-list items" "Production armor B"
    Select-ItemContaining $newModal.Root "Available ARMO and LVLI items" "Production armor B"
    Invoke-Button $newModal.Root "Add selected item to outfit"
    Select-ItemContaining $newModal.Root "Authored outfit equip order" "Production armor B"
    Invoke-Button $newModal.Root "Move outfit item earlier"
    Invoke-Button $newModal.Root "Remove"
    Set-EditValue $newModal.Root "Filter armor and leveled-list items" "ProductionLeveledArmor"
    Select-ItemContaining $newModal.Root "Available ARMO and LVLI items" "ProductionLeveledArmor"
    Invoke-Button $newModal.Root "Add selected item to outfit"
    Select-ItemContaining $newModal.Root "Authored outfit equip order" "ProductionLeveledArmor"
    Set-EditValue $newModal.Root "Deterministic leveled-list reroll seed" "77"
    Invoke-Button $newModal.Root "Reroll LVLI"
    Invoke-Button $newModal.Root "Reset the whole authoring transaction"

    Set-EditValue $newModal.Root "Filter armor and leveled-list items" "Production armor A"
    Select-ItemContaining $newModal.Root "Available ARMO and LVLI items" "Production armor A"
    Invoke-Button $newModal.Root "Add selected item to outfit"
    Set-EditValue $newModal.Root "Filter armor and leveled-list items" "ProductionLeveledArmor"
    Select-ItemContaining $newModal.Root "Available ARMO and LVLI items" "ProductionLeveledArmor"
    Invoke-Button $newModal.Root "Add selected item to outfit"
    Select-ItemContaining $newModal.Root "Authored outfit equip order" "ProductionLeveledArmor"
    Set-EditValue $newModal.Root "Deterministic leveled-list reroll seed" "77"
    Invoke-Button $newModal.Root "Reroll LVLI"
    Capture-Window $newModal.Handle $screenshots.newAuthor
    [void][SkyGui015Native]::SetForegroundWindow($newModal.Handle)
    (Find-Element $newModal.Root ([System.Windows.Automation.ControlType]::Edit) `
        "New outfit EditorID").SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $application $newModal.Handle $newModal.Helper
    Wait-Until -Failure "The new authored OTFT did not reach the parent." -Condition {
        Test-TextContains $mainRoot "New OTFT staged with 2 ordered item"
    }
    $checks.newAddRemoveReorderRerollResetEnterSave = $true

    if ((Test-Path -LiteralPath $newProposal) -or
        (Test-Path -LiteralPath $newPlugin)) {
        throw "The owner-modal Save wrote before explicit Review."
    }
    Invoke-Button $mainRoot "Review outfit proposal"
    Wait-Until -Failure "New OTFT proposal review did not complete." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $newProposal) -and
            -not (Test-Path -LiteralPath $newPlugin) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    $checks.newReviewProposalOnly = $true
    Invoke-Button $mainRoot "Write and verify outfit output"
    Wait-Until -Failure "New OTFT output was not retained after readback." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $newPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.newVerified
    $checks.newWriteAndSecondReadback = $true

    Set-EditValue $mainRoot "Outfit proposal path" $overrideProposal
    Set-EditValue $mainRoot "Outfit output plugin" $overridePlugin
    $overrideModal = Open-OutfitModal $application $mainHandle $mainRoot
    Set-EditValue $overrideModal.Root `
        "Filter outfits by name, plugin, FormID, or item" "ProductionBase"
    Select-ItemContaining $overrideModal.Root "Outfit choices" "ProductionBaseOutfit"
    Press-Button $overrideModal.Root "Override the selected existing outfit" `
        $overrideModal.Handle
    Select-ItemContaining $overrideModal.Root "Authored outfit equip order" "Production armor A"
    Invoke-Button $overrideModal.Root "Move outfit item later"
    Select-ItemContaining $overrideModal.Root "Authored outfit equip order" "ProductionLeveledArmor"
    Set-EditValue $overrideModal.Root "Deterministic leveled-list reroll seed" "99"
    Invoke-Button $overrideModal.Root "Reroll LVLI"
    Capture-Window $overrideModal.Handle $screenshots.overrideAuthor
    Press-Button $overrideModal.Root "Use the selected outfit or save the authored proposal" `
        $overrideModal.Handle
    Wait-ModalClosed $application $overrideModal.Handle $overrideModal.Helper
    Wait-Until -Failure "The override OTFT did not reach the parent." -Condition {
        Test-TextContains $mainRoot "Override OTFT staged with 2 ordered item"
    }
    $checks.overrideReorderAndSave = $true

    Invoke-Button $mainRoot "Review outfit proposal"
    Wait-Until -Failure "Override proposal review did not remain proposal-only." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $overrideProposal) -and
            -not (Test-Path -LiteralPath $overridePlugin) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    $checks.overrideReviewProposalOnly = $true
    Invoke-Button $mainRoot "Write and verify outfit output"
    Wait-Until -Failure "Override OTFT output was not retained after readback." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $overridePlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.overrideVerified
    $checks.overrideWriteAndSecondReadback = $true

    $rawText = (& python $rawVerifier `
        --source-provider $sourceProvider `
        --new-plugin $newPlugin `
        --new-proposal $newProposal `
        --override-plugin $overridePlugin `
        --override-proposal $overrideProposal `
        --report $rawReport 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent raw outfit verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed) {
        throw "Independent raw audit did not prove both exact OTFT records."
    }
    $checks.independentRawNewAndOverrideAudit = $true

    $application.CloseMainWindow() | Out-Null
    if (-not $application.WaitForExit(15000)) {
        throw "The packaged desktop did not close after acceptance."
    }
    $checks.cleanProcessExit = $true

    $publishFiles = @(Get-ChildItem -LiteralPath $publishRootFull -Recurse -File)
    $report = [ordered]@{
        schema = "npcmanager.sky-gui-015.packaged-acceptance.v1"
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
            sourceProvider = Get-RelativeLabPath $sourceProvider
            sourceProviderSha256 = (Get-FileHash -LiteralPath $sourceProvider `
                -Algorithm SHA256).Hash
            sourceBase = Get-RelativeLabPath (Join-Path $dataRoot "OutfitBase.esm")
            sourceBaseSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $dataRoot "OutfitBase.esm") -Algorithm SHA256).Hash
            initialPreviewSeed = 11
            rerolledNewSeed = 77
            rerolledOverrideSeed = 99
        }
        outputs = [ordered]@{
            newProposal = Get-RelativeLabPath $newProposal
            newProposalSha256 = (Get-FileHash -LiteralPath $newProposal `
                -Algorithm SHA256).Hash
            newPlugin = Get-RelativeLabPath $newPlugin
            newPluginSha256 = (Get-FileHash -LiteralPath $newPlugin `
                -Algorithm SHA256).Hash
            overrideProposal = Get-RelativeLabPath $overrideProposal
            overrideProposalSha256 = (Get-FileHash -LiteralPath $overrideProposal `
                -Algorithm SHA256).Hash
            overridePlugin = Get-RelativeLabPath $overridePlugin
            overridePluginSha256 = (Get-FileHash -LiteralPath $overridePlugin `
                -Algorithm SHA256).Hash
            rawAudit = Get-RelativeLabPath $rawReport
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
