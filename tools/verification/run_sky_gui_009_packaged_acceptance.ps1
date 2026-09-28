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
        "03-builds\work\sky-gui-009-desktop-publish-20260722-3"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$acceptedInput = Join-Path $projectRoot `
    "03-builds\work\sky-gui-004-acceptance-input-20260722-1"
$dataRoot = Join-Path $acceptedInput "Data"
$loadOrderPath = Join-Path $acceptedInput "load-order.json"
$sourcePlugin = Join-Path $projectRoot `
    "01-source-copies\gate3-fixtures\identity\Data\M3ArchetypeSSE.esp"
$sourceHash = (Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash
$preflightOutput = Join-Path $projectRoot `
    "03-builds\work\sky-gui-009-preflight-future-$RunTag"
$packageRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-009-gui-package-$RunTag"
$outputPluginName = "M3ArchetypeSSE-SkyGui009.esp"
$outputPlugin = Join-Path $packageRoot "Data\$outputPluginName"
$screenshots = [ordered]@{
    requiredFilterEmpty = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-required-filter-empty-$RunTag.png"
    nullCancelCandidate = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-null-cancel-candidate-$RunTag.png"
    cancelRollback = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-cancel-rollback-$RunTag.png"
    nullKeyboardCandidate = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-null-keyboard-candidate-$RunTag.png"
    parentCommitted = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-parent-committed-$RunTag.png"
    reviewed = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-reviewed-$RunTag.png"
    verified = Join-Path $screenshotRootFull `
        "2026-07-22-SKY-GUI-009-verified-$RunTag.png"
}

foreach ($path in @($executable, $dataRoot, $loadOrderPath, $sourcePlugin)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required acceptance input is absent: $path"
    }
}
foreach ($path in @($preflightOutput, $packageRoot)) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance output exists and will not be overwritten: $path"
    }
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Screenshot evidence exists and will not be overwritten: $path"
    }
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

public static class SkyGui009Native
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

function Get-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    return ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
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

function Select-ListItemContains {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Needle)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    foreach ($item in $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $label = Get-ListItemLabel $item
        if ($label.IndexOf(
            $Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            return $item
        }
    }
    throw "No visible typed row contains '$Needle'."
}

function Get-ListItemLabel {
    param([System.Windows.Automation.AutomationElement]$Item)
    $parts = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($Item.Current.Name)) {
        $parts.Add($Item.Current.Name)
    }
    $textCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    foreach ($text in $Item.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $textCondition)) {
        if (-not [string]::IsNullOrWhiteSpace($text.Current.Name)) {
            $parts.Add($text.Current.Name)
        }
    }
    return ($parts | Select-Object -Unique) -join " | "
}

function Get-SelectedListItemName {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$ListName)
    $list = Find-ElementByName $Root $ListName
    $selected = ([System.Windows.Automation.SelectionPattern]$list.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    if ($selected.Count -ne 1) { return "" }
    return Get-ListItemLabel $selected[0]
}

function Select-FirstTypedRow {
    param([System.Windows.Automation.AutomationElement]$Root)
    $list = Find-ElementByName $Root "Typed record choices"
    [void][SkyGui009Native]::SetForegroundWindow(
        [IntPtr]$Root.Current.NativeWindowHandle)
    $list.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{HOME}")
    $script:firstTypedSelection = ""
    Wait-Until -Failure "The pinned first typed row was not selected." -Condition {
        $script:firstTypedSelection = Get-SelectedListItemName `
            $Root "Typed record choices"
        $script:firstTypedSelection -match "None / NULL|NULL"
    }
    $selected = ([System.Windows.Automation.SelectionPattern]$list.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()
    return $selected[0]
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    [void][SkyGui009Native]::ShowWindow($Handle, 9)
    [void][SkyGui009Native]::SetForegroundWindow($Handle)
    Start-Sleep -Milliseconds 250
    $rect = New-Object SkyGui009Native+Rect
    if (-not [SkyGui009Native]::GetWindowRect($Handle, [ref]$rect)) {
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
        try {
            $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        }
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
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    try {
        $script:modalHandle = [IntPtr]::Zero
        Wait-Until -Failure "Owned modal '$WindowTitle' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            foreach ($row in [SkyGui009Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $OwnerHandle -and
                    ([string]::IsNullOrEmpty($WindowTitle) -or
                        $parts[1] -eq $WindowTitle)) {
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
    Wait-Until -Failure "Owned modal did not close." `
        -TimeoutSeconds $TimeoutSeconds -Condition {
        -not ([SkyGui009Native]::VisibleWindowsForProcess([uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Choose-SourcePlugin {
    param([System.Diagnostics.Process]$Application, [IntPtr]$MainHandle,
        [System.Windows.Automation.AutomationElement]$MainRoot, [string]$Path)
    $session = Start-OwnedModal $Application $MainHandle `
        "Browse existing NPC source plugin" "" 45
    try {
        [void][SkyGui009Native]::SetForegroundWindow($session.Handle)
        Start-Sleep -Milliseconds 250
        [System.Windows.Forms.SendKeys]::SendWait("%n")
        [System.Windows.Forms.SendKeys]::SendWait("^a")
        [System.Windows.Forms.SendKeys]::SendWait($Path)
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Wait-ModalClosed $Application $session.Handle $session.Helper 45
    }
    catch {
        if (-not $session.Helper.HasExited) {
            Stop-Process -Id $session.Helper.Id -Force
        }
        throw
    }
    Wait-Until -Failure "The source picker did not bind the selected plugin hash." `
        -TimeoutSeconds 60 -Condition {
        (Get-EditValue $MainRoot "Existing NPC source plugin") -eq $Path -and
            (Get-EditValue $MainRoot "Existing NPC source hash") -eq $sourceHash
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
$activeHelpers = New-Object `
    System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $application = Start-Process -FilePath $executable -PassThru
    Wait-Until -Failure "The packaged desktop did not create a main window." `
        -TimeoutSeconds 30 -Condition {
        $application.Refresh()
        $application.MainWindowHandle -ne 0
    }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    [void][SkyGui009Native]::ShowWindow($mainHandle, 9)
    [void][SkyGui009Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The existing-NPC task did not enable after review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Edit existing NPC task").Current.IsEnabled
    }

    Select-Tab $mainRoot "Edit existing NPC task"
    Choose-SourcePlugin $application $mainHandle $mainRoot $sourcePlugin
    Set-EditValue $mainRoot "Existing NPC FormID" "0x00000800"
    Set-EditValue $mainRoot "Existing NPC output plugin filename" $outputPluginName
    Set-EditValue $mainRoot "Existing NPC output folder" $packageRoot
    Invoke-Button $mainRoot "Load existing NPC fields"
    Wait-Until -Failure "The exact source NPC did not load." -TimeoutSeconds 90 `
        -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Edit existing NPC identity and archetype").Current.IsEnabled
    }
    $checks.hashBoundSourceLoaded = $true

    $identity = Start-OwnedModal $application $mainHandle `
        "Edit existing NPC identity and archetype" `
        "Edit NPC identity and archetype" 180
    $activeHelpers.Add($identity.Helper)

    $racePicker = Start-OwnedModal $application $identity.Handle `
        "Choose NPC race" "Choose the NPC race" 60
    $activeHelpers.Add($racePicker.Helper)
    Set-EditValue $racePicker.Root "Filter typed records" `
        "NoSuchRequiredRaceRecord009"
    Wait-Until -Failure "Required picker did not expose its filtered-empty state." `
        -Condition {
        (Test-TextContains $racePicker.Root "No typed records match") -and
            -not (Find-Element $racePicker.Root `
                ([System.Windows.Automation.ControlType]::Button) `
                "Select typed record").Current.IsEnabled
    }
    Capture-Window $racePicker.Handle $screenshots.requiredFilterEmpty
    Invoke-Button $racePicker.Root "Cancel typed record selection"
    Wait-ModalClosed $application $racePicker.Handle $racePicker.Helper
    $checks.requiredFilterEmpty = $true

    $voiceCancel = Start-OwnedModal $application $identity.Handle `
        "Choose NPC voice type" "Choose the NPC voice type" 60
    $activeHelpers.Add($voiceCancel.Helper)
    [void](Select-FirstTypedRow $voiceCancel.Root)
    Capture-Window $voiceCancel.Handle $screenshots.nullCancelCandidate
    Invoke-Button $voiceCancel.Root "Cancel typed record selection"
    Wait-ModalClosed $application $voiceCancel.Handle $voiceCancel.Helper

    $voiceAccepted = Start-OwnedModal $application $identity.Handle `
        "Choose NPC voice type" "Choose the NPC voice type" 60
    $activeHelpers.Add($voiceAccepted.Helper)
    $reopenedSelection = Get-SelectedListItemName `
        $voiceAccepted.Root "Typed record choices"
    if ($reopenedSelection -notmatch "M3VoiceSSE|0x00000802") {
        throw "Picker Cancel did not restore the original Voice selection: '$reopenedSelection'."
    }
    Capture-Window $voiceAccepted.Handle $screenshots.cancelRollback
    $nullRow = Select-FirstTypedRow $voiceAccepted.Root
    Capture-Window $voiceAccepted.Handle $screenshots.nullKeyboardCandidate
    [void][SkyGui009Native]::SetForegroundWindow($voiceAccepted.Handle)
    $nullRow.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $application $voiceAccepted.Handle $voiceAccepted.Helper
    $checks.cancelRollback = $true
    $checks.keyboardAcceptance = $true

    Capture-Window $identity.Handle $screenshots.parentCommitted
    Invoke-Button $identity.Root "Save identity"
    Wait-ModalClosed $application $identity.Handle $identity.Helper
    $checks.parentCommit = $true

    if (Test-Path -LiteralPath $packageRoot) {
        throw "The production UI wrote output before review."
    }
    Invoke-Button $mainRoot "Review existing NPC changes"
    Wait-Until -Failure "The Voice change review did not become current." `
        -TimeoutSeconds 90 -Condition {
        (Test-TextContains $mainRoot "Ready to create") -and
            (Test-TextContains $mainRoot "Voice")
    }
    Capture-Window $mainHandle $screenshots.reviewed
    $checks.noWriteBeforeReview = -not (Test-Path -LiteralPath $packageRoot)

    Invoke-Button $mainRoot "Create verified existing NPC override"
    Wait-Until -Failure "The verified SKY-GUI-009 package was not retained." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath (Join-Path $packageRoot "npcmanager-package.json")) -and
            (Test-Path -LiteralPath $outputPlugin) -and
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.verified
    $checks.productionWriteCompleted = $true

    $manifest = Join-Path $packageRoot "npcmanager-package.json"
    $proposal = Join-Path $packageRoot "evidence\npc-edit-proposal.json"
    $verificationPath = Join-Path $packageRoot `
        "evidence\npc-edit-verification.json"
    $dotnet = Join-Path $labRoot `
        "tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe"
    $cli = Join-Path $projectRoot `
        "src\NpcManager.Cli\bin\Release\net10.0\npcm.dll"
    $verifyText = (& $dotnet $cli package verify --manifest $manifest `
        --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent package verification failed: $verifyText"
    }
    $packageVerification = $verifyText | ConvertFrom-Json
    if (-not $packageVerification.verified) {
        throw "Package verifier did not report verified=true."
    }
    $pluginText = (& $dotnet $cli plugin verify --edition skyrimse `
        --before $sourcePlugin --after $outputPlugin --proposal $proposal `
        --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent plugin verification failed: $pluginText"
    }
    $pluginVerification = $pluginText | ConvertFrom-Json
    if (-not $pluginVerification.isValid) {
        throw "Plugin verifier did not report isValid=true."
    }
    $rawVerification = Get-Content -Raw -LiteralPath $verificationPath |
        ConvertFrom-Json
    if (-not $rawVerification.valid -or -not $rawVerification.trueOverride -or
        $rawVerification.majorRecordCount -ne 1 -or
        $rawVerification.npcRecordCount -ne 1 -or
        $rawVerification.sourceOwnedTargetCount -ne 1 -or
        $rawVerification.selfOwnedTargetCount -ne 0) {
        throw "Raw readback did not prove exactly one source-owned NPC override."
    }
    $changes = @($rawVerification.observedChanges)
    if ($changes.Count -ne 1 -or $changes[0].field -ne "Voice" -or
        $changes[0].before -ne "M3ArchetypeSSE.esp|0x00000802" -or
        $changes[0].after -ne "none") {
        throw "Independent readback did not prove the exact typed Voice clear."
    }
    $checks.independentPackageVerification = $true
    $checks.independentPluginReadback = $true

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
    [ordered]@{
        result = "PASS"
        surfaceId = "SKY-GUI-009"
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable `
            -Algorithm SHA256).Hash
        source = [ordered]@{
            plugin = Get-RelativeLabPath $sourcePlugin
            sha256 = $sourceHash
            targetFormId = "0x00000800"
        }
        package = [ordered]@{
            root = Get-RelativeLabPath $packageRoot
            manifest = Get-RelativeLabPath $manifest
            manifestSha256 = (Get-FileHash -LiteralPath $manifest `
                -Algorithm SHA256).Hash
            plugin = Get-RelativeLabPath $outputPlugin
            pluginSha256 = (Get-FileHash -LiteralPath $outputPlugin `
                -Algorithm SHA256).Hash
            packageVerified = [bool]$packageVerification.verified
            pluginVerified = [bool]$pluginVerification.isValid
            observedChanges = $changes
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
        [void][SkyGui009Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
