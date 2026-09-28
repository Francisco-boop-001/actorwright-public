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
$manifest = Join-Path $projectRoot "tools\fixtures\gui\sky-gui-003-animations-sse.json"
$expectedManifestHash = "78DD8E4A8B521D6CB9FA5DF6A896892F154EB513873A3DEAC4FFD2DE148C2835"
$screenshots = [ordered]@{
    femaleFirstPerson = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-003-female-first-person.png"
    select = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-003-select.png"
    enter = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-003-enter.png"
    doubleClick = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-003-double-click.png"
    cancel = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-003-cancel.png"
}

foreach ($path in @($executable, $manifest)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required acceptance input is absent: $path"
    }
}
$actualManifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
if ($actualManifestHash -ne $expectedManifestHash) {
    throw "Animation manifest hash drifted: $actualManifestHash"
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence exists and will not be overwritten: $path"
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

public static class SkyGui003Native
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
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

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
        [int]$TimeoutSeconds = 15
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

function Try-FindElement {
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
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
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

function Set-Checked {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [bool]$Checked
    )
    $box = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $toggle = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $desired = if ($Checked) {
        [System.Windows.Automation.ToggleState]::On
    }
    else {
        [System.Windows.Automation.ToggleState]::Off
    }
    if ($toggle.Current.ToggleState -ne $desired) {
        $toggle.Toggle()
    }
    Wait-Until -Failure "Checkbox '$Name' did not reach $desired." -Condition {
        $current = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        $current.Current.ToggleState -eq $desired
    }
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
    $invoke = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
}

function Select-Item {
    param([System.Windows.Automation.AutomationElement]$Element)
    $selection = [System.Windows.Automation.SelectionItemPattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $selection.Select()
}

function Get-ButtonEnabled {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    return (Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name).Current.IsEnabled
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

function Capture-Window {
    param(
        [IntPtr]$Handle,
        [string]$Path
    )
    $rect = New-Object SkyGui003Native+Rect
    if (-not [SkyGui003Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not resolve the visible window rectangle for $Handle."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
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

function Start-AnimationModal {
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
        'Open animation picker')))
`$button = `$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'Animation picker button is absent or disabled.'
}
`$pattern = [System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
`$pattern.Invoke()
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    try {
        Wait-Until -Failure "The packaged animation modal did not become visible." -Condition {
            $rows = [SkyGui003Native]::VisibleWindowsForProcess([uint32]$Application.Id)
            foreach ($row in $rows) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $MainHandle -and $parts[1] -eq "Choose an animation") {
                    $script:acceptedAnimationHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        $modalHandle = $script:acceptedAnimationHandle
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
    Wait-Until -Failure "The animation modal did not close." -Condition {
        $rows = [SkyGui003Native]::VisibleWindowsForProcess([uint32]$Application.Id)
        return -not ($rows | Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(3000)) {
        throw "The animation modal opener remained blocked after close."
    }
}

function Wait-Leaf {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $script:acceptedLeaf = $null
    Wait-Until -Failure "Animation leaf '$Name' did not become visible." -Condition {
        $candidate = Try-FindElement $Root ([System.Windows.Automation.ControlType]::TreeItem) $Name
        if ($null -eq $candidate) { return $false }
        $script:acceptedLeaf = $candidate
        return $true
    }
    return $script:acceptedLeaf
}

function Wait-CatalogLoaded {
    param([System.Windows.Automation.AutomationElement]$Root)
    Wait-Until -Failure "The exact animation catalog did not finish loading." -Condition {
        Test-TextContains $Root $expectedManifestHash
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

    $tab = Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) "Animation browser"
    Select-Item $tab
    Set-EditValue $mainRoot "Animation catalog file" $manifest
    Set-Checked $mainRoot "Animation target NPC is female" $true
    Wait-Until -Failure "The animation picker entry point did not enable." -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) "Open animation picker").Current.IsEnabled
    }

    $session = Start-AnimationModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    Set-Checked $session.Root "Show first-person and camera animations" $true
    Set-EditValue $session.Root "Filter animations" "power"
    $powerLeaf = Wait-Leaf $session.Root "Power Attack; animations/Weapon/attack.hkx"
    Select-Item $powerLeaf
    if (-not (Get-ButtonEnabled $session.Root "Select animation")) {
        throw "The female first-person leaf did not become acceptable."
    }
    Capture-Window $session.Handle $screenshots.femaleFirstPerson
    Invoke-Button $session.Root "Cancel animation selection"
    Wait-ModalClosed $application $session.Handle $session.Helper
    $checks.femaleAndFirstPersonFilters = $true

    Set-Checked $mainRoot "Animation target NPC is female" $false
    $session = Start-AnimationModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    Set-EditValue $session.Root "Filter animations" ""
    $branch = Wait-Leaf $session.Root "Gestures & Dialogue (IDLE) (1)"
    Select-Item $branch
    if (Get-ButtonEnabled $session.Root "Select animation") {
        throw "A hierarchy branch became acceptable."
    }
    Set-EditValue $session.Root "Filter animations" "talk"
    $talkLeaf = Wait-Leaf $session.Root "Talk Gesture; animations/Dialogue/talk.hkx"
    Select-Item $talkLeaf
    Capture-Window $session.Handle $screenshots.select
    Invoke-Button $session.Root "Select animation"
    Wait-ModalClosed $application $session.Handle $session.Helper
    Wait-Until -Failure "Select did not commit Talk Gesture to the caller." -Condition {
        Test-TextContains $mainRoot "Talk Gesture"
    }
    $checks.selectButton = $true

    $session = Start-AnimationModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    Set-EditValue $session.Root "Filter animations" "walk"
    $walkLeaf = Wait-Leaf $session.Root "Walk Forward; animations/MT/Neutral/walk.hkx"
    Select-Item $walkLeaf
    $walkLeaf.SetFocus()
    Capture-Window $session.Handle $screenshots.enter
    [void][SkyGui003Native]::SetForegroundWindow($session.Handle)
    [void][SkyGui003Native]::PostMessage(
        $session.Handle, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero)
    [void][SkyGui003Native]::PostMessage(
        $session.Handle, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Wait-Until -Failure "Enter did not commit Walk Forward to the caller." -Condition {
        Test-TextContains $mainRoot "Walk Forward"
    }
    $checks.enter = $true

    $session = Start-AnimationModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    Set-EditValue $session.Root "Filter animations" "talk"
    $talkLeaf = Wait-Leaf $session.Root "Talk Gesture; animations/Dialogue/talk.hkx"
    Select-Item $talkLeaf
    $talkLeaf.SetFocus()
    Capture-Window $session.Handle $screenshots.doubleClick
    $rectangle = $talkLeaf.Current.BoundingRectangle
    $x = [int]($rectangle.Left + ($rectangle.Width / 2))
    $y = [int]($rectangle.Top + ($rectangle.Height / 2))
    [void][SkyGui003Native]::SetForegroundWindow($session.Handle)
    [void][SkyGui003Native]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 120
    [SkyGui003Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui003Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [SkyGui003Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui003Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Wait-Until -Failure "Double-click did not commit Talk Gesture to the caller." -Condition {
        Test-TextContains $mainRoot "Talk Gesture"
    }
    $checks.doubleClick = $true

    $session = Start-AnimationModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    Set-EditValue $session.Root "Filter animations" "walk"
    $walkLeaf = Wait-Leaf $session.Root "Walk Forward; animations/MT/Neutral/walk.hkx"
    Select-Item $walkLeaf
    Capture-Window $session.Handle $screenshots.cancel
    Invoke-Button $session.Root "Cancel animation selection"
    Wait-ModalClosed $application $session.Handle $session.Helper
    if (-not (Test-TextContains $mainRoot "Talk Gesture")) {
        throw "Cancel changed the caller's committed Talk Gesture selection."
    }
    $checks.cancelRollback = $true

    $screenshotEvidence = foreach ($entry in $screenshots.GetEnumerator()) {
        $item = Get-Item -LiteralPath $entry.Value
        [ordered]@{
            state = $entry.Key
            path = $item.FullName.Substring($projectRoot.Length + 1).Replace("\", "/")
            size = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        }
    }
    [ordered]@{
        result = "PASS"
        surfaceId = "SKY-GUI-003"
        packagedExecutable = $executable.Substring($projectRoot.Length + 1).Replace("\", "/")
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        manifest = $manifest.Substring($projectRoot.Length + 1).Replace("\", "/")
        manifestSha256 = $actualManifestHash
        finalSelection = "Talk Gesture; animations/Dialogue/talk.hkx"
        checks = $checks
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
        protectedLiveRootTouched = $false
    } | ConvertTo-Json -Depth 8
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui003Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)
        if (-not $application.WaitForExit(3000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
