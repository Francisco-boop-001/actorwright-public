param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260722-8",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-011-012-desktop-publish-20260722-5"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-011-012-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-011-012-input-$RunTag"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$preflightOutput = Join-Path $inputRoot "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-011-012-output-$RunTag"
$proposalPath = Join-Path $outputRoot "face-edit-proposal.json"
$outputPlugin = Join-Path $outputRoot "FaceEditFixtureSSE-FaceEdited.esp"
$sourcePlugin = Join-Path $dataRoot "FaceEditFixtureSSE.esp"
$sourceFixture = Join-Path $projectRoot `
    "03-builds\work\sky-gui-011-012-fixture-20260722-1\Data\FaceEditFixtureSSE.esp"
$raceMenuSource = Join-Path $projectRoot `
    "01-source-copies\racemenu-paint-catalog-real\Data"
$skyrimMasterSource = "F:\ExampleGame\Game Root\Data\Skyrim.esm"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_sky_gui_011_012_face_edit_output.py"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-011-012-face-edit-output-raw-audit-$RunTag.json"
$acceptedP04 = Join-Path $projectRoot `
    "03-builds\work\sky-gui-010-input-20260722-10\Data\P04SSE.esp"
$frozenZip = Join-Path $projectRoot `
    "04-packages\NpcManagerReimplementation-Emi-v0.1-runtime-test.zip"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-012-loaded-neutral-$RunTag.png"
    faceParts = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-face-parts-$RunTag.png"
    paintNoSelection = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-paint-no-selection-$RunTag.png"
    paintSelected = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-paint-selected-$RunTag.png"
    paintDoubleClick = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-paint-double-click-$RunTag.png"
    paintCleared = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-paint-cleared-$RunTag.png"
    paintCancelled = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-011-paint-cancelled-$RunTag.png"
    editorCancel = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-whole-editor-cancel-$RunTag.png"
    raceMenuSliders = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-racemenu-sliders-$RunTag.png"
    sculpt = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-sculpt-$RunTag.png"
    facePaint = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-face-paint-$RunTag.png"
    nativeMorph = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-native-morph-staged-$RunTag.png"
    parentStaged = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-parent-staged-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-reviewed-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-012-verified-$RunTag.png"
}

$required = @(
    $executable,
    $sourceFixture,
    $skyrimMasterSource,
    (Join-Path $raceMenuSource "RaceMenu.esp"),
    (Join-Path $raceMenuSource "RaceMenu.bsa"),
    $rawVerifier,
    $acceptedP04,
    $frozenZip
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
Copy-Item -LiteralPath $sourceFixture -Destination $sourcePlugin
Copy-Item -LiteralPath $skyrimMasterSource `
    -Destination (Join-Path $dataRoot "Skyrim.esm")
Copy-Item -LiteralPath (Join-Path $raceMenuSource "RaceMenu.esp") `
    -Destination (Join-Path $dataRoot "RaceMenu.esp")
Copy-Item -LiteralPath (Join-Path $raceMenuSource "RaceMenu.bsa") `
    -Destination (Join-Path $dataRoot "RaceMenu.bsa")
$expectedInputHashes = [ordered]@{
    "Skyrim.esm" = `
        "06A9881F6AB277AFD2A82E71F8A3719183B7031DB4D9C78A7F40C08E1E4FFA91"
    "FaceEditFixtureSSE.esp" = `
        "F3C7B3AC9391515953A1F860AC65CA5663BD3DB6A1F23AAE16903D28749DA86A"
    "RaceMenu.esp" = `
        "15E009B7F219B1E95BD78A31CDF8BE2D3B45A38B784DD04F34A71A4AB6F76602"
    "RaceMenu.bsa" = `
        "FCC46F42731D2B7C3782B96CFF40930FFCF689482CA8E9A5568CF2B76E9A7603"
}
foreach ($entry in $expectedInputHashes.GetEnumerator()) {
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
        @{ name = "FaceEditFixtureSSE.esp"; order = 1; enabled = $true },
        @{ name = "RaceMenu.esp"; order = 2; enabled = $true }
    )
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $loadOrderPath, $loadOrder, [Text.UTF8Encoding]::new($false))
$sourceHash = (Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui011012Native
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
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
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
    if ($null -eq $element) {
        throw "Required $($ControlType.ProgrammaticName) '$Name' was not found."
    }
    return $element
}

function Find-ElementByName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) { throw "Required element '$Name' was not found." }
    return $element
}

function Invoke-Button {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $script:buttonToInvoke = $null
    Wait-Until -Failure "Required button '$Name' did not render." -Condition {
        $script:buttonToInvoke = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::Button) $Name
        $null -ne $script:buttonToInvoke
    }
    if (-not $script:buttonToInvoke.Current.IsEnabled) {
        throw "Required button '$Name' is disabled."
    }
    ([System.Windows.Automation.InvokePattern]$script:buttonToInvoke.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
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
    if ($pattern.Current.IsReadOnly) { throw "Edit '$Name' is read-only." }
    $pattern.SetValue($Value)
    Wait-Until -Failure "Edit '$Name' did not retain the requested value." -Condition {
        ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value -eq $Value
    }
}

function Set-RangeValue {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [double]$Value)
    $slider = Find-Element $Root ([System.Windows.Automation.ControlType]::Slider) $Name
    $pattern = [System.Windows.Automation.RangeValuePattern]$slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $pattern.SetValue($Value)
    Wait-Until -Failure "Slider '$Name' did not retain $Value." -Condition {
        [Math]::Abs((([System.Windows.Automation.RangeValuePattern]$slider.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value) - $Value) `
            -lt 0.0001
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

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    try {
        $window = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)
        $window.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    }
    catch { }
    $rect = New-Object SkyGui011012Native+Rect
    $restored = $false
    foreach ($ignored in 1..20) {
        [void][SkyGui011012Native]::ShowWindow($Handle, 9)
        [void][SkyGui011012Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui011012Native]::PostMessage(
            $Handle, 0x0112, [IntPtr]0xF120, [IntPtr]::Zero)
        [void][SkyGui011012Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui011012Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui011012Native]::IsIconic($Handle) -and
            ($rect.Right - $rect.Left) -ge 640 -and
            ($rect.Bottom - $rect.Top) -ge 400) {
            $restored = $true
            break
        }
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if (-not $restored) {
        throw "Screenshot target did not restore ($width x $height)."
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            $screenContext = [SkyGui011012Native]::GetDC([IntPtr]::Zero)
            try {
                if ($screenContext -eq [IntPtr]::Zero -or
                    -not [SkyGui011012Native]::BitBlt(
                        $deviceContext, 0, 0, $width, $height,
                        $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                    if (-not [SkyGui011012Native]::PrintWindow(
                            $Handle, $deviceContext, 0)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui011012Native]::ReleaseDC(
                        [IntPtr]::Zero, $screenContext)
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
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
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
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    try {
        $script:modalHandle = [IntPtr]::Zero
        Wait-Until -Failure "Owned modal '$WindowTitlePrefix' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            foreach ($row in [SkyGui011012Native]::VisibleWindowsForProcess(
                    [uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $OwnerHandle -and
                    $parts[1].StartsWith(
                        $WindowTitlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $script:modalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        return [pscustomobject]@{
            Handle = $script:modalHandle
            Root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $script:modalHandle)
            Helper = $helper
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
        -not ([SkyGui011012Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Get-ElementTextLabel {
    param([System.Windows.Automation.AutomationElement]$Element)
    $parts = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($Element.Current.Name)) {
        $parts.Add($Element.Current.Name)
    }
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($child in $Element.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if (-not [string]::IsNullOrWhiteSpace($child.Current.Name)) {
            $parts.Add($child.Current.Name)
        }
    }
    return [string]::Join(" | ", $parts)
}

function Get-ListRows {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$ListName)
    $list = Find-ElementByName $Root $ListName
    $dataItem = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    $listItem = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $condition = New-Object System.Windows.Automation.OrCondition($dataItem, $listItem)
    return $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Select-ListRowContaining {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ListName, [string]$Needle)
    $script:matchingRow = $null
    Wait-Until -Failure "Row containing '$Needle' did not render." -Condition {
        foreach ($row in Get-ListRows $Root $ListName) {
            if ((Get-ElementTextLabel $row).IndexOf(
                    $Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $script:matchingRow = $row
                return $true
            }
        }
        return $false
    }
    ([System.Windows.Automation.SelectionItemPattern]$script:matchingRow.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $script:matchingRow.SetFocus()
    return $script:matchingRow
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
    Wait-Until -Failure "The packaged desktop did not create a main window." -Condition {
        $application.Refresh(); $application.MainWindowHandle -ne 0
    }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    [void][SkyGui011012Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui011012Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The face-edit task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Edit Skyrim NPC face task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Edit Skyrim NPC face task"
    Set-EditValue $mainRoot "Face source plugin" "FaceEditFixtureSSE.esp"
    Set-EditValue $mainRoot "Face NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Face proposal path" $proposalPath
    Set-EditValue $mainRoot "Face output plugin path" $outputPlugin
    Invoke-Button $mainRoot "Load complete face document"
    Wait-Until -Failure "The complete face baseline did not load neutrally." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open six-section face editor").Current.IsEnabled -and
            (Test-TextContains $mainRoot "Loaded without changes")
    }
    if (Test-TextContains $mainRoot "Change staged") {
        throw "Loading the complete source staged an implicit face mutation."
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.neutralHashBoundLoad = $true

    $cancelEditor = Start-OwnedModal $application $mainHandle `
        "Open six-section face editor" "Edit Skyrim face" 180
    $activeHelpers.Add($cancelEditor.Helper)
    [void][SkyGui011012Native]::ShowWindow($cancelEditor.Handle, 3)
    Capture-Window $cancelEditor.Handle $screenshots.faceParts
    Select-Tab $cancelEditor.Root "Native morphs"
    Select-Tab $cancelEditor.Root "RaceMenu sliders"
    Select-Tab $cancelEditor.Root "Tints"

    $selectedPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Choose mask..." "Choose warpaint paint" 180
    $activeHelpers.Add($selectedPicker.Helper)
    Set-EditValue $selectedPicker.Root "Filter registered paints" "Argonian Stripes 01"
    [void](Select-ListRowContaining $selectedPicker.Root `
        "Registered RaceMenu paints" "Argonian Stripes 01")
    Set-EditValue $selectedPicker.Root "Filter registered paints" "NoSuchPaint011"
    Wait-Until -Failure "Filtered paint state did not clear the stale selection." `
        -Condition {
        $rows = @(Get-ListRows $selectedPicker.Root "Registered RaceMenu paints")
        $select = Find-Element $selectedPicker.Root `
            ([System.Windows.Automation.ControlType]::Button) "Select registered paint"
        $rows.Count -eq 1 -and
            (Get-ElementTextLabel $rows[0]).IndexOf(
                "None", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            -not $select.Current.IsEnabled
    }
    Capture-Window $selectedPicker.Handle $screenshots.paintNoSelection
    Set-EditValue $selectedPicker.Root "Filter registered paints" "Argonian Stripes 01"
    $paintRow = Select-ListRowContaining $selectedPicker.Root `
        "Registered RaceMenu paints" "Argonian Stripes 01"
    Capture-Window $selectedPicker.Handle $screenshots.paintSelected
    [void][SkyGui011012Native]::SetForegroundWindow($selectedPicker.Handle)
    $paintRow.SetFocus()
    [void][SkyGui011012Native]::PostMessage(
        $selectedPicker.Handle, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero)
    [void][SkyGui011012Native]::PostMessage(
        $selectedPicker.Handle, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero)
    Wait-ModalClosed $application $selectedPicker.Handle $selectedPicker.Helper
    Wait-Until -Failure "Exact paint selection did not reach the tint caller." -Condition {
        Test-TextContains $cancelEditor.Root "ArgonianStripes01.dds"
    }
    $checks.paintExactSelection = $true
    $checks.paintNoSelectionVeto = $true
    $checks.paintKeyboardAcceptance = $true

    $clearPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Choose mask..." "Choose warpaint paint" 180
    $activeHelpers.Add($clearPicker.Helper)
    Set-EditValue $clearPicker.Root "Filter registered paints" ""
    $clearRow = Select-ListRowContaining $clearPicker.Root `
        "Registered RaceMenu paints" "None"
    Invoke-Button $clearPicker.Root "Select registered paint"
    Wait-ModalClosed $application $clearPicker.Handle $clearPicker.Helper
    Wait-Until -Failure "Explicit paint clear did not remove the mask path." -Condition {
        -not (Test-TextContains $cancelEditor.Root "ArgonianStripes01.dds")
    }
    Capture-Window $cancelEditor.Handle $screenshots.paintCleared
    $checks.paintExplicitClear = $true
    $checks.paintOkAcceptance = $true

    $doublePicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Choose mask..." "Choose warpaint paint" 180
    $activeHelpers.Add($doublePicker.Helper)
    Set-EditValue $doublePicker.Root "Filter registered paints" "Argonian Stripes 01"
    $doubleRow = Select-ListRowContaining $doublePicker.Root `
        "Registered RaceMenu paints" "Argonian Stripes 01"
    $doubleRow.SetFocus()
    Capture-Window $doublePicker.Handle $screenshots.paintDoubleClick
    $rectangle = $doubleRow.Current.BoundingRectangle
    if ($rectangle.Width -le 0 -or $rectangle.Height -le 0) {
        throw "The selected paint row has no visible double-click target."
    }
    $x = [int]($rectangle.Left + ($rectangle.Width / 2))
    $y = [int]($rectangle.Top + ($rectangle.Height / 2))
    [void][SkyGui011012Native]::SetForegroundWindow($doublePicker.Handle)
    [void][SkyGui011012Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [SkyGui011012Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui011012Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [SkyGui011012Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui011012Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Wait-ModalClosed $application $doublePicker.Handle $doublePicker.Helper
    Wait-Until -Failure "Paint double-click did not reach the tint caller." -Condition {
        Test-TextContains $cancelEditor.Root "ArgonianStripes01.dds"
    }
    $checks.paintDoubleClickAcceptance = $true

    $cancelPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Choose mask..." "Choose warpaint paint" 180
    $activeHelpers.Add($cancelPicker.Helper)
    Set-EditValue $cancelPicker.Root "Filter registered paints" "Argonian Stripes 01"
    [void](Select-ListRowContaining $cancelPicker.Root `
        "Registered RaceMenu paints" "Argonian Stripes 01")
    Invoke-Button $cancelPicker.Root "Cancel paint selection"
    Wait-ModalClosed $application $cancelPicker.Handle $cancelPicker.Helper
    if (-not (Test-TextContains $cancelEditor.Root "ArgonianStripes01.dds")) {
        throw "Paint picker Cancel did not preserve the caller's prior selection."
    }
    Capture-Window $cancelEditor.Handle $screenshots.paintCancelled
    $checks.paintCancelRollback = $true

    Select-Tab $cancelEditor.Root "RaceMenu sculpt"
    Select-Tab $cancelEditor.Root "RaceMenu face paint"
    Invoke-Button $cancelEditor.Root "Cancel"
    Wait-ModalClosed $application $cancelEditor.Handle $cancelEditor.Helper
    Wait-Until -Failure "Whole-editor Cancel did not preserve the neutral parent." -Condition {
        (Test-TextContains $mainRoot "Loaded without changes") -and
            -not (Test-TextContains $mainRoot "Change staged")
    }
    Capture-Window $mainHandle $screenshots.editorCancel
    $checks.wholeEditorCancelRollback = $true

    $acceptedEditor = Start-OwnedModal $application $mainHandle `
        "Open six-section face editor" "Edit Skyrim face" 180
    $activeHelpers.Add($acceptedEditor.Helper)
    [void][SkyGui011012Native]::ShowWindow($acceptedEditor.Handle, 3)
    Select-Tab $acceptedEditor.Root "RaceMenu sliders"
    Capture-Window $acceptedEditor.Handle $screenshots.raceMenuSliders
    Select-Tab $acceptedEditor.Root "Tints"
    Select-Tab $acceptedEditor.Root "RaceMenu sculpt"
    Capture-Window $acceptedEditor.Handle $screenshots.sculpt
    Select-Tab $acceptedEditor.Root "RaceMenu face paint"
    Capture-Window $acceptedEditor.Handle $screenshots.facePaint
    Select-Tab $acceptedEditor.Root "Face parts"
    Select-Tab $acceptedEditor.Root "Native morphs"
    Set-RangeValue $acceptedEditor.Root "Nose length" 0.25
    Capture-Window $acceptedEditor.Handle $screenshots.nativeMorph
    Invoke-Button $acceptedEditor.Root "Save face"
    Wait-ModalClosed $application $acceptedEditor.Handle $acceptedEditor.Helper
    Wait-Until -Failure "Native face edit did not stage in the parent." -Condition {
        Test-TextContains $mainRoot "Change staged"
    }
    Capture-Window $mainHandle $screenshots.parentStaged
    $checks.sixSectionsVisible = $true
    $checks.nativeMorphStaged = $true

    if ((Test-Path -LiteralPath $proposalPath) -or
        (Test-Path -LiteralPath $outputPlugin)) {
        throw "The parent wrote a proposal or ESP before explicit review."
    }
    Invoke-Button $mainRoot "Review face proposal"
    Wait-Until -Failure "The face proposal did not become current." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $proposalPath) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    if (Test-Path -LiteralPath $outputPlugin) {
        throw "Proposal review wrote the ESP early."
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.reviewWritesProposalOnly = $true

    Invoke-Button $mainRoot "Write and verify fresh face plugin"
    Wait-Until -Failure "The verified face output was not retained." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $outputPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verified
    $checks.productionWriteAndVerify = $true

    $rawText = (& python $rawVerifier --source $sourcePlugin `
        --output $outputPlugin --proposal $proposalPath `
        --report $rawReport 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent raw face-output verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed -or
        @($rawAudit.observed.nam9_changed_indices).Count -ne 1 -or
        [int]$rawAudit.observed.nam9_changed_indices[0] -ne 0) {
        throw "Independent raw face-output report did not prove the sole NAM9 delta."
    }
    $checks.independentRawReadback = $true

    $outputHash = (Get-FileHash -LiteralPath $outputPlugin -Algorithm SHA256).Hash
    $proposalHash = (Get-FileHash -LiteralPath $proposalPath -Algorithm SHA256).Hash
    $p04Hash = (Get-FileHash -LiteralPath $acceptedP04 -Algorithm SHA256).Hash
    $zipHash = (Get-FileHash -LiteralPath $frozenZip -Algorithm SHA256).Hash
    if ($p04Hash -ne `
        "040F0282F4020FA7EF8BC648F9D1BA7957EBA6484A9ACDC5E27A3A1F67231E87") {
        throw "Accepted SKY-GUI-010 P04 input drifted."
    }
    if ($zipHash -ne `
        "5039764CEACFC1569B1B011056D9C7249CB7FD2B649A076B7C595D4A5249DC74") {
        throw "Frozen Emi runtime-test ZIP drifted."
    }
    $checks.acceptedArtifactsPreserved = $true

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
        surfaceIds = @("SKY-GUI-011", "SKY-GUI-012")
        runTag = $RunTag
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable `
            -Algorithm SHA256).Hash
        copiedInput = [ordered]@{
            dataRoot = Get-RelativeLabPath $dataRoot
            sourcePlugin = Get-RelativeLabPath $sourcePlugin
            sourceSha256 = $sourceHash
            skyrimEsmSha256 = $expectedInputHashes["Skyrim.esm"]
            raceMenuEspSha256 = $expectedInputHashes["RaceMenu.esp"]
            raceMenuBsaSha256 = $expectedInputHashes["RaceMenu.bsa"]
            targetFormId = "0x00000800"
        }
        proposal = [ordered]@{
            path = Get-RelativeLabPath $proposalPath
            sha256 = $proposalHash
            wroteEspDuringReview = $false
        }
        output = [ordered]@{
            plugin = Get-RelativeLabPath $outputPlugin
            sha256 = $outputHash
            semanticDelta = "NAM9 slider index 0: 0.0 -> 0.25"
            preservedSourceAppearance = $true
            sourceOwnedNpcOverrideCount = 1
            selfOwnedNpcTargetCount = 0
            wrldCount = 0
            cellCount = 0
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        rawAudit = Get-RelativeLabPath $rawReport
        runtimeAuthority = $false
        facegenAuthority = $false
        textureRenderAuthority = $false
        visualAuthority = $false
        protectedLiveRootReadOnlySource = `
            "F:\ExampleGame\Game Root\Data\Skyrim.esm"
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
        [void][SkyGui011012Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
