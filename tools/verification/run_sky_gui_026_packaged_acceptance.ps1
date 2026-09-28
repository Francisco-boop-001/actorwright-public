param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot "03-builds\work\sky-gui-001-desktop-publish-20260722-36"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$settingsFile = Join-Path $projectRoot "03-builds\work\npc-studio-settings\preview-lighting.json"
$screenshots = [ordered]@{
    invalid = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-026-invalid.png"
    valid = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-026-valid.png"
    applied = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-026-applied.png"
    reopened = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-026-reopened.png"
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Packaged desktop executable is absent: $executable"
}
if (Test-Path -LiteralPath $settingsFile) {
    throw "Acceptance requires an absent settings file and will not overwrite: $settingsFile"
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence already exists and will not be overwritten: $path"
    }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui026Native
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
    param(
        [scriptblock]$Condition,
        [string]$Failure,
        [int]$TimeoutSeconds = 12
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Find-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType,
        [string]$Name
    )
    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $element) {
        throw "Required $($ControlType.ProgrammaticName) '$Name' was not found."
    }
    return $element
}

function Invoke-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $button = Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) {
        throw "Required button '$Name' is disabled."
    }
    $pattern = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-ButtonEnabled {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    return (Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name).Current.IsEnabled
}

function Set-EditValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [string]$Value
    )
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Get-EditValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Test-TextContains {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Needle
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    foreach ($text in $texts) {
        if ($text.Current.Name.IndexOf(
                $Needle,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
}

function Get-ListItemCount {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition).Count
}

function Capture-Window {
    param(
        [IntPtr]$Handle,
        [string]$Path
    )
    $rect = New-Object SkyGui026Native+Rect
    if (-not [SkyGui026Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not resolve the visible window rectangle for $Handle."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "Visible window $Handle has an invalid rectangle."
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $rect.Left,
                $rect.Top,
                0,
                0,
                (New-Object System.Drawing.Size($width, $height)))
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Start-LightingModal {
    param(
        [System.Diagnostics.Process]$Application,
        [IntPtr]$MainHandle
    )
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
        [System.Windows.Automation.AutomationElement]::NameProperty,
        'Open preview lighting editor')))
`$button = `$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button) { throw 'Lighting button was not found.' }
`$pattern = [System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
`$pattern.Invoke()
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    $modalHandle = [IntPtr]::Zero
    try {
        Wait-Until -Failure "The packaged lighting modal did not become visible." -Condition {
            $rows = [SkyGui026Native]::VisibleWindowsForProcess([uint32]$Application.Id)
            foreach ($row in $rows) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $MainHandle -and $parts[1] -eq "Preview lighting") {
                    $script:acceptedModalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        $modalHandle = $script:acceptedModalHandle
        return [pscustomobject]@{
            Handle = $modalHandle
            Root = [System.Windows.Automation.AutomationElement]::FromHandle($modalHandle)
            Helper = $helper
        }
    }
    catch {
        if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
        throw
    }
}

function Wait-ModalClosed {
    param(
        [System.Diagnostics.Process]$Application,
        [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper
    )
    Wait-Until -Failure "The packaged lighting modal did not close." -Condition {
        $rows = [SkyGui026Native]::VisibleWindowsForProcess([uint32]$Application.Id)
        return -not ($rows | Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(3000)) {
        throw "The modal opener remained blocked after the modal closed."
    }
}

function Assert-HashUnchanged {
    param(
        [string]$Expected,
        [string]$Context
    )
    $actual = (Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        throw "$Context changed the persisted settings file."
    }
}

$application = $null
$activeHelpers = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $application = Start-Process -FilePath $executable -PassThru
    Wait-Until -Failure "The packaged desktop did not create a main window." -TimeoutSeconds 20 -Condition {
        $application.Refresh()
        return $application.MainWindowHandle -ne 0
    }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    $session = Start-LightingModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    if ($session.Root.Current.Name -ne "Preview lighting editor") {
        throw "The visible owned window was not the production lighting editor."
    }
    $checks.ownerModal = $true

    Set-EditValue $session.Root "Ambient intensity 0 to 4" "0.4"
    Invoke-Button $session.Root "Reset rig"
    Wait-Until -Failure "Reset did not restore ambient 0.2." -Condition {
        (Get-EditValue $session.Root "Ambient intensity 0 to 4") -eq "0.2"
    }
    $checks.reset = $true

    Invoke-Button $session.Root "Add preview light"
    Wait-Until -Failure "Add did not create a second light." -Condition {
        (Get-ListItemCount $session.Root) -eq 2
    }
    Set-EditValue $session.Root "Selected light ID" "rim"
    Invoke-Button $session.Root "Move up"
    Invoke-Button $session.Root "Move down"
    Invoke-Button $session.Root "Move up"
    Invoke-Button $session.Root "Remove"
    Wait-Until -Failure "Remove did not return to one light." -Condition {
        (Get-ListItemCount $session.Root) -eq 1
    }
    Invoke-Button $session.Root "Add preview light"
    Set-EditValue $session.Root "Selected light ID" "rim"
    Invoke-Button $session.Root "Move up"
    $checks.addRemoveReorder = $true

    Set-EditValue $session.Root "Ambient intensity 0 to 4" "NaN"
    Wait-Until -Failure "Non-finite input did not disable Apply." -Condition {
        -not (Get-ButtonEnabled $session.Root "Apply and persist preview lighting")
    }
    Wait-Until -Failure "Non-finite input did not expose an actionable validation message." -Condition {
        Test-TextContains $session.Root "finite"
    }
    Capture-Window $session.Handle $screenshots.invalid
    $checks.nonFiniteRefused = $true

    Set-EditValue $session.Root "Ambient intensity 0 to 4" "0.65"
    Wait-Until -Failure "Valid ambient input did not refresh the visible preview." -Condition {
        (Get-ButtonEnabled $session.Root "Apply and persist preview lighting") -and
        (Test-TextContains $session.Root "ambient 0.65")
    }
    Capture-Window $session.Handle $screenshots.valid
    $checks.immediatePreview = $true

    Invoke-Button $session.Root "Apply and persist preview lighting"
    Wait-ModalClosed $application $session.Handle $session.Helper
    Wait-Until -Failure "Apply did not create the exact settings file." -Condition {
        Test-Path -LiteralPath $settingsFile -PathType Leaf
    }
    $settingsHash = (Get-FileHash -LiteralPath $settingsFile -Algorithm SHA256).Hash
    $settings = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
    if ($settings.schemaVersion -ne 1 -or
        $settings.kind -ne "npc-studio-preview-lighting" -or
        [double]$settings.preset.ambientIntensity -ne 0.65 -or
        $settings.preset.lights.Count -ne 2 -or
        $settings.preset.lights[0].id -ne "rim" -or
        $settings.preset.lights[1].id -ne "key") {
        throw "Persisted settings did not reopen with the exact accepted preset."
    }
    Wait-Until -Failure "The main shell did not display the committed lighting summary." -Condition {
        Test-TextContains $mainRoot "2 light(s)"
    }
    Capture-Window $mainHandle $screenshots.applied
    $checks.applyReadback = $true

    $session = Start-LightingModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    if ((Get-EditValue $session.Root "Ambient intensity 0 to 4") -ne "0.65" -or
        (Get-EditValue $session.Root "Selected light ID") -ne "rim" -or
        (Get-ListItemCount $session.Root) -ne 2) {
        throw "The packaged modal did not reopen the exact persisted preset."
    }
    Capture-Window $session.Handle $screenshots.reopened
    Set-EditValue $session.Root "Ambient intensity 0 to 4" "1.25"
    Invoke-Button $session.Root "Cancel preview lighting edit"
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-HashUnchanged $settingsHash "Cancel"
    $checks.cancelRollback = $true

    $session = Start-LightingModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Set-EditValue $session.Root "Ambient intensity 0 to 4" "1.5"
    [void][SkyGui026Native]::PostMessage(
        $session.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-HashUnchanged $settingsHash "Title close"
    $checks.titleCloseRollback = $true

    $session = Start-LightingModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Set-EditValue $session.Root "Ambient intensity 0 to 4" "1.75"
    [void][SkyGui026Native]::PostMessage(
        $session.Handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    [void][SkyGui026Native]::PostMessage(
        $session.Handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-HashUnchanged $settingsHash "Escape"
    $checks.escapeRollback = $true

    $finalSettings = Get-Content -Raw -LiteralPath $settingsFile | ConvertFrom-Json
    if ([double]$finalSettings.preset.ambientIntensity -ne 0.65 -or
        $finalSettings.preset.lights[0].id -ne "rim" -or
        $finalSettings.preset.lights[1].id -ne "key") {
        throw "Rollback journeys changed the independently reopened final preset."
    }

    $screenshotEvidence = foreach ($entry in $screenshots.GetEnumerator()) {
        $item = Get-Item -LiteralPath $entry.Value
        [ordered]@{
            state = $entry.Key
            path = $item.FullName.Substring($projectRoot.Length + 1).Replace("\", "/")
            size = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    $result = [ordered]@{
        result = "PASS"
        surfaceId = "SKY-GUI-026"
        packagedExecutable = $executable.Substring($projectRoot.Length + 1).Replace("\", "/")
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        settingsPath = $settingsFile.Substring($projectRoot.Length + 1).Replace("\", "/")
        settingsSha256 = $settingsHash
        acceptedAmbient = 0.65
        acceptedLightIds = @("rim", "key")
        checks = $checks
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
        protectedLiveRootTouched = $false
    }
    $result | ConvertTo-Json -Depth 8
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui026Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)
        if (-not $application.WaitForExit(3000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
