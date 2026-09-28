param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260722-5",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-013-desktop-publish-20260722-3"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-013-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-013-input-$RunTag"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$preflightOutput = Join-Path $inputRoot "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-013-output-$RunTag"
$proposalPath = Join-Path $outputRoot "body-edit-proposal.json"
$outputPlugin = Join-Path $outputRoot "FaceEditFixtureSSE-BodyEdited.esp"
$sourcePlugin = Join-Path $dataRoot "FaceEditFixtureSSE.esp"
$sourceFixture = Join-Path $projectRoot `
    "03-builds\work\sky-gui-011-012-fixture-20260722-1\Data\FaceEditFixtureSSE.esp"
$raceMenuSource = Join-Path $projectRoot `
    "01-source-copies\racemenu-paint-catalog-real\Data"
$skyrimMasterSource = "F:\ExampleGame\Game Root\Data\Skyrim.esm"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_sky_gui_013_body_output.py"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-013-body-output-raw-audit-$RunTag.json"
$frozenZip = Join-Path $projectRoot `
    "04-packages\NpcManagerReimplementation-Emi-v0.1-runtime-test.zip"
$emiScreenshot = Join-Path $labRoot "Resources\Screenshot\NpcStudioEmiTrial.png"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-loaded-neutral-$RunTag.png"
    weight = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-weight-$RunTag.png"
    bodySlide = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-bodyslide-$RunTag.png"
    transforms = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-transforms-$RunTag.png"
    skin = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-skin-$RunTag.png"
    bodyPaint = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-body-paint-$RunTag.png"
    paintEmpty = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-paint-empty-$RunTag.png"
    escapeCancel = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-escape-cancel-$RunTag.png"
    titleClose = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-title-close-$RunTag.png"
    staged = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-parent-staged-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-reviewed-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-013-verified-$RunTag.png"
}

$required = @(
    $executable,
    $sourceFixture,
    $skyrimMasterSource,
    (Join-Path $raceMenuSource "RaceMenu.esp"),
    (Join-Path $raceMenuSource "RaceMenu.bsa"),
    $rawVerifier,
    $frozenZip,
    $emiScreenshot
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

public static class SkyGui013Native
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

function Select-Tab {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $tab = Find-Element $Root ([System.Windows.Automation.ControlType]::TabItem) $Name
    ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Wait-Until -Failure "Tab '$Name' did not become selected." -Condition {
        ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    }
    Start-Sleep -Milliseconds 160
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
    $rect = New-Object SkyGui013Native+Rect
    foreach ($ignored in 1..20) {
        [void][SkyGui013Native]::ShowWindow($Handle, 9)
        [void][SkyGui013Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui013Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui013Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui013Native]::IsIconic($Handle) -and
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
            $screenContext = [SkyGui013Native]::GetDC([IntPtr]::Zero)
            try {
                if ($screenContext -eq [IntPtr]::Zero -or
                    -not [SkyGui013Native]::BitBlt(
                        $deviceContext, 0, 0, $width, $height,
                        $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                    if (-not [SkyGui013Native]::PrintWindow(
                            $Handle, $deviceContext, 0)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui013Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
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
            foreach ($row in [SkyGui013Native]::VisibleWindowsForProcess(
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
        -not ([SkyGui013Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Select-ComboItem {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ComboName, [string]$ItemName)
    $combo = Find-Element $Root ([System.Windows.Automation.ControlType]::ComboBox) $ComboName
    $selection = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    $combo.SetFocus()
    foreach ($ignored in 1..4) {
        $current = @($selection.Current.GetSelection())
        if ($current.Count -eq 1) {
            $currentName = $current[0].Current.Name
            if ($currentName -eq $ItemName -or
                $currentName.IndexOf(
                    "Label = $ItemName", [StringComparison]::Ordinal) -ge 0) {
                return
            }
        }
        [System.Windows.Forms.SendKeys]::SendWait("{DOWN}")
        Start-Sleep -Milliseconds 180
    }
    $current = @($selection.Current.GetSelection())
    throw "Combo '$ComboName' did not select '$ItemName'; current='$($current[0].Current.Name)'."
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
    [void][SkyGui013Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui013Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The body-edit task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Edit Skyrim NPC body task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Edit Skyrim NPC body task"
    Set-EditValue $mainRoot "Body source plugin" "FaceEditFixtureSSE.esp"
    Set-EditValue $mainRoot "Body NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Body proposal path" $proposalPath
    Set-EditValue $mainRoot "Body output plugin path" $outputPlugin
    Invoke-Button $mainRoot "Load complete body document"
    Wait-Until -Failure "The complete body baseline did not load neutrally." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open five-section body editor").Current.IsEnabled -and
            (Test-TextContains $mainRoot "Loaded without changes")
    }
    if (Test-TextContains $mainRoot "Change staged") {
        throw "Loading the complete source staged an implicit body mutation."
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.neutralHashBoundLoad = $true

    $cancelEditor = Start-OwnedModal $application $mainHandle `
        "Open five-section body editor" "Edit Skyrim body" 180
    $activeHelpers.Add($cancelEditor.Helper)
    [void][SkyGui013Native]::ShowWindow($cancelEditor.Handle, 3)
    Set-RangeValue $cancelEditor.Root "Skyrim NPC NAM7 weight" 60
    Capture-Window $cancelEditor.Handle $screenshots.weight
    Invoke-Button $cancelEditor.Root "Reset this section"
    $weightSlider = Find-Element $cancelEditor.Root `
        ([System.Windows.Automation.ControlType]::Slider) "Skyrim NPC NAM7 weight"
    Wait-Until -Failure "Weight section reset did not restore exact NAM7 55." -Condition {
        [Math]::Abs((([System.Windows.Automation.RangeValuePattern]$weightSlider.GetCurrentPattern(
            [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value) - 55) `
            -lt 0.0001
    }
    $checks.weightSectionReset = $true
    Select-Tab $cancelEditor.Root "BodySlide"
    Capture-Window $cancelEditor.Handle $screenshots.bodySlide
    Select-Tab $cancelEditor.Root "Node transforms"
    Capture-Window $cancelEditor.Handle $screenshots.transforms
    Select-Tab $cancelEditor.Root "Skin overrides"
    Capture-Window $cancelEditor.Handle $screenshots.skin
    Select-Tab $cancelEditor.Root "Body paint"
    Capture-Window $cancelEditor.Handle $screenshots.bodyPaint

    $bodyPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Add registered paint..." "Choose body paint" 180
    $activeHelpers.Add($bodyPicker.Helper)
    Wait-Until -Failure "The honest empty body-paint state did not render." -Condition {
        (Test-TextContains $bodyPicker.Root "No registered body paint") -and
            -not (Find-Element $bodyPicker.Root `
                ([System.Windows.Automation.ControlType]::Button) `
                "Select registered paint").Current.IsEnabled
    }
    Capture-Window $bodyPicker.Handle $screenshots.paintEmpty
    Invoke-Button $bodyPicker.Root "Cancel paint selection"
    Wait-ModalClosed $application $bodyPicker.Handle $bodyPicker.Helper
    Select-ComboItem $cancelEditor.Root "Body paint zone" "Hands"
    $handsPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Add registered paint..." "Choose hands paint" 180
    $activeHelpers.Add($handsPicker.Helper)
    Invoke-Button $handsPicker.Root "Cancel paint selection"
    Wait-ModalClosed $application $handsPicker.Handle $handsPicker.Helper
    Select-ComboItem $cancelEditor.Root "Body paint zone" "Feet"
    $feetPicker = Start-OwnedModal $application $cancelEditor.Handle `
        "Add registered paint..." "Choose feet paint" 180
    $activeHelpers.Add($feetPicker.Helper)
    Invoke-Button $feetPicker.Root "Cancel paint selection"
    Wait-ModalClosed $application $feetPicker.Handle $feetPicker.Helper
    $checks.bodyHandsFeetPickerCallers = $true
    $checks.emptyCatalogNoSelectionVeto = $true

    Select-Tab $cancelEditor.Root "Weight"
    Set-RangeValue $cancelEditor.Root "Skyrim NPC NAM7 weight" 60
    [void][SkyGui013Native]::SetForegroundWindow($cancelEditor.Handle)
    [void][SkyGui013Native]::PostMessage(
        $cancelEditor.Handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    [void][SkyGui013Native]::PostMessage(
        $cancelEditor.Handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-ModalClosed $application $cancelEditor.Handle $cancelEditor.Helper
    Wait-Until -Failure "Escape did not roll the whole body editor back." -Condition {
        (Test-TextContains $mainRoot "Loaded without changes") -and
            -not (Test-TextContains $mainRoot "Change staged")
    }
    Capture-Window $mainHandle $screenshots.escapeCancel
    $checks.escapeCancelRollback = $true

    $closeEditor = Start-OwnedModal $application $mainHandle `
        "Open five-section body editor" "Edit Skyrim body" 180
    $activeHelpers.Add($closeEditor.Helper)
    Set-RangeValue $closeEditor.Root "Skyrim NPC NAM7 weight" 60
    [void][SkyGui013Native]::PostMessage(
        $closeEditor.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeEditor.Handle $closeEditor.Helper
    Wait-Until -Failure "Title-bar close did not roll the body editor back." -Condition {
        (Test-TextContains $mainRoot "Loaded without changes") -and
            -not (Test-TextContains $mainRoot "Change staged")
    }
    Capture-Window $mainHandle $screenshots.titleClose
    $checks.titleBarCloseRollback = $true

    $acceptedEditor = Start-OwnedModal $application $mainHandle `
        "Open five-section body editor" "Edit Skyrim body" 180
    $activeHelpers.Add($acceptedEditor.Helper)
    [void][SkyGui013Native]::ShowWindow($acceptedEditor.Handle, 3)
    Select-Tab $acceptedEditor.Root "BodySlide"
    Select-Tab $acceptedEditor.Root "Node transforms"
    Select-Tab $acceptedEditor.Root "Skin overrides"
    Select-Tab $acceptedEditor.Root "Body paint"
    Select-Tab $acceptedEditor.Root "Weight"
    Set-RangeValue $acceptedEditor.Root "Skyrim NPC NAM7 weight" 60
    Invoke-Button $acceptedEditor.Root "Accept body document"
    Wait-ModalClosed $application $acceptedEditor.Handle $acceptedEditor.Helper
    Wait-Until -Failure "The accepted NAM7 edit did not stage in the parent." -Condition {
        Test-TextContains $mainRoot "Change staged"
    }
    Capture-Window $mainHandle $screenshots.staged
    $checks.fiveSectionsVisible = $true
    $checks.nam7Staged = $true

    if ((Test-Path -LiteralPath $proposalPath) -or
        (Test-Path -LiteralPath $outputPlugin)) {
        throw "The parent wrote a proposal or plugin before explicit review."
    }
    Invoke-Button $mainRoot "Review body proposal"
    Wait-Until -Failure "The body proposal did not become current." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $proposalPath) -and
            (Test-TextContains $mainRoot "Ready to write")
    }
    if (Test-Path -LiteralPath $outputPlugin) {
        throw "Proposal review wrote the plugin early."
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.reviewWritesProposalOnly = $true

    Invoke-Button $mainRoot "Write and verify fresh body plugin"
    Wait-Until -Failure "The verified body output was not retained." `
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
        throw "Independent raw body-output verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed -or
        [double]$rawAudit.observed.nam7_before -ne 55 -or
        [double]$rawAudit.observed.nam7_after -ne 60) {
        throw "Independent raw report did not prove the sole NAM7 delta."
    }
    $checks.independentRawReadback = $true

    $outputHash = (Get-FileHash -LiteralPath $outputPlugin -Algorithm SHA256).Hash
    $proposalHash = (Get-FileHash -LiteralPath $proposalPath -Algorithm SHA256).Hash
    $zipHash = (Get-FileHash -LiteralPath $frozenZip -Algorithm SHA256).Hash
    if ($zipHash -ne `
        "5039764CEACFC1569B1B011056D9C7249CB7FD2B649A076B7C595D4A5249DC74") {
        throw "Frozen Emi runtime-test ZIP drifted."
    }
    $checks.frozenArtifactPreserved = $true

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
        surfaceIds = @("SKY-GUI-013")
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
            wrotePluginDuringReview = $false
        }
        output = [ordered]@{
            plugin = Get-RelativeLabPath $outputPlugin
            sha256 = $outputHash
            semanticDelta = "NPC_.NAM7: 55.0 -> 60.0"
            sourceOwnedNpcOverrideCount = 1
            selfOwnedNpcTargetCount = 0
            wrldCount = 0
            cellCount = 0
            vmadCount = 0
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        rawAudit = Get-RelativeLabPath $rawReport
        qualitativeCurrentModScreenshot = [ordered]@{
            path = Get-RelativeLabPath $emiScreenshot
            sha256 = (Get-FileHash -LiteralPath $emiScreenshot -Algorithm SHA256).Hash
            demonstrates = "current Emi mod renders in game with intact gross face and hair geometry"
            providerBoundToSyntheticNam7Fixture = $false
            inFrameControlNpc = $false
        }
        bodySlideBuildAuthority = $false
        meshAuthority = $false
        textureRenderAuthority = $false
        runtimeAuthority = $false
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
        [void][SkyGui013Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
