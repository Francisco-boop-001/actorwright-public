param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260722-1"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-010-desktop-publish-20260722-7"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-010-input-$RunTag"
$generatorRoot = Join-Path $inputRoot "generator"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$preflightOutput = Join-Path $inputRoot "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-010-output-$RunTag"
$outputPlugin = Join-Path $outputRoot "P04SSE.esp"
$sourcePlugin = Join-Path $dataRoot "P04SSE.esp"
$dotnet = Join-Path $labRoot `
    "tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe"
$generator = Join-Path $projectRoot `
    "tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll"
$realHair = Join-Path $projectRoot `
    "01-source-copies\gate2-emi2\headpart-geometry-bases\meshes\KS Hairdo's\Lassi.nif"
$rawVerifier = Join-Path $projectRoot `
    "tools\verification\verify_npc_face_patch.py"
$cli = Join-Path $projectRoot "src\NpcManager.Cli\bin\Release\net10.0\npcm.dll"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-loaded-neutral-$RunTag.png"
    filteredEmpty = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-filtered-empty-$RunTag.png"
    preview = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-off-engine-preview-$RunTag.png"
    cancelRollback = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-cancel-rollback-$RunTag.png"
    parentCommitted = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-parent-committed-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-reviewed-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-010-verified-$RunTag.png"
}

foreach ($path in @($executable, $dotnet, $generator, $realHair, $rawVerifier, $cli)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required acceptance input is absent: $path"
    }
}
foreach ($path in @($inputRoot, $preflightOutput, $outputRoot)) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance path exists and will not be overwritten: $path"
    }
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Screenshot evidence exists and will not be overwritten: $path"
    }
}

New-Item -ItemType Directory -Path $generatorRoot -Force | Out-Null
$generatedFo4 = Join-Path $generatorRoot "P04FO4.esp"
$generatedSse = Join-Path $generatorRoot "P04SSE.esp"
& $dotnet $generator --headparts $generatedFo4 $generatedSse
if ($LASTEXITCODE -ne 0) { throw "The typed head-part fixture generator failed." }
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
Copy-Item -LiteralPath $generatedSse -Destination $sourcePlugin
Copy-Item -LiteralPath (Join-Path $generatorRoot "data\meshes") `
    -Destination (Join-Path $dataRoot "meshes") -Recurse
Copy-Item -LiteralPath $realHair `
    -Destination (Join-Path $dataRoot "meshes\p04\hair-alt.nif") -Force
$loadOrderJson = @{
    schemaVersion = 1
    edition = "skyrimse"
    plugins = @(@{ name = "P04SSE.esp"; order = 0; enabled = $true })
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText(
    $loadOrderPath, $loadOrderJson, [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
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

public static class SkyGui010Native
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
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
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

function Test-HeadPartPreviewSettled {
    param([System.Windows.Automation.AutomationElement]$Root)
    $image = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::Image) `
        "Off-engine selected head part preview"
    $accept = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::Button) `
        "Select compatible head part"
    return $null -ne $image -and $null -ne $accept -and $accept.Current.IsEnabled
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    [void][SkyGui010Native]::ShowWindow($Handle, 9)
    [void][SkyGui010Native]::SetForegroundWindow($Handle)
    Start-Sleep -Milliseconds 250
    $rect = New-Object SkyGui010Native+Rect
    if (-not [SkyGui010Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read screenshot window bounds."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -lt 640 -or $height -lt 400) {
        throw "Screenshot target is too small: $width x $height."
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }
        finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $bitmap.Dispose() }
}

function Start-OwnedModal {
    param([System.Diagnostics.Process]$Application, [IntPtr]$OwnerHandle,
        [string]$ButtonName, [string]$WindowTitle, [int]$TimeoutSeconds = 120)
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
        [System.Windows.Automation.AutomationElement]::NameProperty, '$ButtonName')))
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
        Wait-Until -Failure "Owned modal '$WindowTitle' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            foreach ($row in [SkyGui010Native]::VisibleWindowsForProcess([uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $OwnerHandle -and $parts[1] -eq $WindowTitle) {
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
    }
    catch {
        if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
        throw
    }
}

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper, [int]$TimeoutSeconds = 45)
    Wait-Until -Failure "Owned modal did not close." -TimeoutSeconds $TimeoutSeconds -Condition {
        -not ([SkyGui010Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
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
    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($textElement in $Element.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $textCondition)) {
        if (-not [string]::IsNullOrWhiteSpace($textElement.Current.Name)) {
            $parts.Add($textElement.Current.Name)
        }
    }
    return [string]::Join(" | ", $parts)
}

function Select-ExactHeadPartRow {
    param([System.Windows.Automation.AutomationElement]$Root, [IntPtr]$Handle)
    $list = Find-ElementByName $Root "Compatible Skyrim head parts"
    $dataItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $rowCondition = New-Object System.Windows.Automation.OrCondition(
        $dataItemCondition, $listItemCondition)
    $script:exactHeadPartRow = $null
    Wait-Until -Failure "The exact typed alternate-hair row was not rendered." -Condition {
        foreach ($row in $list.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $rowCondition)) {
            $label = Get-ElementTextLabel $row
            if ($label.IndexOf("P04AlternateHairSSE", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
                $label.IndexOf("0x00000823", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $script:exactHeadPartRow = $row
                return $true
            }
        }
        return $false
    }
    [void][SkyGui010Native]::SetForegroundWindow($Handle)
    $selection = [System.Windows.Automation.SelectionItemPattern]`
        $script:exactHeadPartRow.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
    $script:exactHeadPartRow.SetFocus()
    Wait-Until -Failure "The exact typed alternate-hair row was not selected." -Condition {
        ([System.Windows.Automation.SelectionItemPattern]`
            $script:exactHeadPartRow.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    }
    return $script:exactHeadPartRow
}

function Test-ExactParentHeadPartRow {
    param([System.Windows.Automation.AutomationElement]$Root)
    $list = Find-ElementByName $Root "Ordered staged NPC head parts"
    $dataItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::DataItem)
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $rowCondition = New-Object System.Windows.Automation.OrCondition(
        $dataItemCondition, $listItemCondition)
    foreach ($row in $list.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $rowCondition)) {
        $label = Get-ElementTextLabel $row
        if ($label.IndexOf("hair", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $label.IndexOf("P04SSE.esp", [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            $label.IndexOf("0x00000823", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
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
    [void][SkyGui010Native]::ShowWindow($mainHandle, 9)
    [void][SkyGui010Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The head-part task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Edit Skyrim NPC head parts task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Edit Skyrim NPC head parts task"
    Set-EditValue $mainRoot "Head-part source plugin" "P04SSE.esp"
    Set-EditValue $mainRoot "Head-part NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Head-part output plugin path" $outputPlugin
    Invoke-Button $mainRoot "Load exact NPC head parts"
    Wait-Until -Failure "The exact PNAM baseline did not load neutrally." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Open compatible head-part picker").Current.IsEnabled -and
            (Test-TextContains $mainRoot "Loaded without changes")
    }
    if (Test-TextContains $mainRoot "Change staged") {
        throw "Loading the real source staged an implicit PNAM mutation."
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.neutralHashBoundLoad = $true

    $cancelPicker = Start-OwnedModal $application $mainHandle `
        "Open compatible head-part picker" "Choose hair" 180
    $activeHelpers.Add($cancelPicker.Helper)
    Set-EditValue $cancelPicker.Root "Filter compatible head parts" "NoSuchHair010"
    Wait-Until -Failure "The picker did not expose its filtered-empty state." -Condition {
        (Test-TextContains $cancelPicker.Root "No compatible head parts match") -and
            -not (Find-Element $cancelPicker.Root `
                ([System.Windows.Automation.ControlType]::Button) `
                "Select compatible head part").Current.IsEnabled
    }
    Capture-Window $cancelPicker.Handle $screenshots.filteredEmpty
    Set-EditValue $cancelPicker.Root "Filter compatible head parts" "AlternateHair"
    [void](Select-ExactHeadPartRow $cancelPicker.Root $cancelPicker.Handle)
    Wait-Until -Failure "The real off-engine preview did not complete." `
        -TimeoutSeconds 180 -Condition {
        Test-HeadPartPreviewSettled $cancelPicker.Root
    }
    Capture-Window $cancelPicker.Handle $screenshots.preview
    Invoke-Button $cancelPicker.Root "Cancel head part selection"
    Wait-ModalClosed $application $cancelPicker.Handle $cancelPicker.Helper
    Wait-Until -Failure "Picker Cancel did not preserve the neutral parent." -Condition {
        (Test-TextContains $mainRoot "Loaded without changes") -and
            -not (Test-TextContains $mainRoot "Change staged")
    }
    Capture-Window $mainHandle $screenshots.cancelRollback
    $checks.filteredEmpty = $true
    $checks.offEnginePreview = $true
    $checks.cancelRollback = $true

    $acceptedPicker = Start-OwnedModal $application $mainHandle `
        "Open compatible head-part picker" "Choose hair" 180
    $activeHelpers.Add($acceptedPicker.Helper)
    Set-EditValue $acceptedPicker.Root "Filter compatible head parts" "AlternateHair"
    $headPartRow = Select-ExactHeadPartRow $acceptedPicker.Root $acceptedPicker.Handle
    Wait-Until -Failure "The accepted preview did not settle." -TimeoutSeconds 180 -Condition {
        Test-HeadPartPreviewSettled $acceptedPicker.Root
    }
    [void][SkyGui010Native]::SetForegroundWindow($acceptedPicker.Handle)
    $headPartRow.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $application $acceptedPicker.Handle $acceptedPicker.Helper
    Capture-Window $mainHandle $screenshots.parentCommitted
    Wait-Until -Failure "Keyboard acceptance did not stage the typed hair row." -Condition {
        (Test-TextContains $mainRoot "Change staged") -and
            (Test-ExactParentHeadPartRow $mainRoot)
    }
    $checks.keyboardAcceptance = $true
    $checks.parentCommit = $true

    if (Test-Path -LiteralPath $outputPlugin) {
        throw "The parent wrote output before review."
    }
    Invoke-Button $mainRoot "Review exact head-part change"
    Wait-Until -Failure "The exact PNAM review did not become current." `
        -TimeoutSeconds 90 -Condition {
        (Test-TextContains $mainRoot "Ready to write") -and
            (Test-TextContains $mainRoot "PNAM")
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.noWriteBeforeReview = -not (Test-Path -LiteralPath $outputPlugin)

    Invoke-Button $mainRoot "Write verified head-part plugin"
    Wait-Until -Failure "The verified head-part output was not retained." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $outputPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verified
    $checks.productionWriteCompleted = $true

    $outputHash = (Get-FileHash -LiteralPath $outputPlugin -Algorithm SHA256).Hash
    $rawEvidence = Join-Path $outputRoot "gui-face-patch-evidence.json"
    @{ applied = $true; outputSha256 = $outputHash } |
        ConvertTo-Json | Set-Content -LiteralPath $rawEvidence -Encoding utf8
    $rawText = (& python $rawVerifier --before $sourcePlugin --after $outputPlugin `
        --json $rawEvidence --form-id 0x800 `
        --headparts "0x811,0x812,0x823,0x814,0x815,0x816,0x817,0x818,0x819" `
        --hair-color 0x802 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $rawText -notmatch "RESULT PASS") {
        throw "Independent raw PNAM verification failed: $rawText"
    }
    $catalogText = (& $dotnet $cli headpart choices --edition skyrimse `
        --data-root $dataRoot --plugins P04SSE.esp `
        --race "P04SSE.esp|0x801" --sex female --type hair --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "Independent catalog CLI failed: $catalogText" }
    $catalog = $catalogText | ConvertFrom-Json
    $alternate = @($catalog.candidates | Where-Object {
        $_.reference -eq "P04SSE.esp|0x00000823"
    })
    if ($alternate.Count -ne 1 -or
        $alternate[0].modelNif -ne "meshes/p04/hair-alt.nif") {
        throw "Independent catalog did not reopen the accepted typed hair candidate."
    }
    $checks.independentCatalogReadback = $true
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
        surfaceId = "SKY-GUI-010"
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        source = [ordered]@{
            plugin = Get-RelativeLabPath $sourcePlugin
            sha256 = $sourceHash
            targetFormId = "0x00000800"
            baselineHair = "P04SSE.esp|0x00000813"
        }
        output = [ordered]@{
            plugin = Get-RelativeLabPath $outputPlugin
            sha256 = $outputHash
            acceptedHair = "P04SSE.esp|0x00000823"
            pnamOrder = @("0x811", "0x812", "0x823", "0x814", "0x815", "0x816", "0x817", "0x818", "0x819")
            unrelatedFieldsPreserved = $true
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
        visualAuthority = $false
        protectedLiveRootTouched = $false
    } | ConvertTo-Json -Depth 12
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui010Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
