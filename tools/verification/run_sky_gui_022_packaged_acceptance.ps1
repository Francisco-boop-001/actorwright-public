param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260723-1",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-022-desktop-publish-20260723-1"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-022-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$sourceOptions = Join-Path $projectRoot `
    "tools\fixtures\sky-gui-022\skyrim-options.json"
$sourceHash = (Get-FileHash -LiteralPath $sourceOptions `
    -Algorithm SHA256).Hash
$fixtureRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-022-native-facetint-fixture-20260723-3"
$dataRoot = Join-Path $fixtureRoot "Data"
$workRoot = Join-Path $projectRoot `
    "03-builds\work\sky-gui-022-packaged-$RunTag"
$proposalPath = Join-Path $workRoot "options-proposal.json"
$outputRoot = Join-Path $workRoot "ProductionOutput"
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-022-packaged-independent-$RunTag.json"
$negativeReport = Join-Path $projectRoot `
    "05-reports\sky-gui-022-packaged-negative-control-$RunTag.json"
$screenshots = [ordered]@{
    texture = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-texture.png"
    conventions = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-conventions.png"
    validation = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-validation.png"
    order = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-order.png"
    authority = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-authority.png"
    proposal = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-proposal.png"
    final = Join-Path $screenshotRootFull `
        "$RunTag-SKY-GUI-022-final.png"
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Packaged desktop executable is absent: $executable"
}
if (-not (Test-Path -LiteralPath $sourceOptions -PathType Leaf)) {
    throw "Opening options fixture is absent: $sourceOptions"
}
if (-not (Test-Path -LiteralPath $dataRoot -PathType Container)) {
    throw "Native fixture Data root is absent: $dataRoot"
}
foreach ($path in @($workRoot, $reportFull, $rawReport, $negativeReport) +
    @($screenshots.Values)) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance output already exists and will not be overwritten: $path"
    }
}
New-Item -ItemType Directory -Path $workRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui022Native
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
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height, bool repaint);

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

function New-ControlCondition {
    param(
        [System.Windows.Automation.ControlType]$ControlType,
        [string]$Name
    )
    return New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
}

function Find-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType,
        [string]$Name
    )
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ControlCondition $ControlType $Name))
    if ($null -eq $element) {
        throw "Required $($ControlType.ProgrammaticName) '$Name' was not found."
    }
    return $element
}

function Find-Elements {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [System.Windows.Automation.ControlType]$ControlType,
        [string]$Name
    )
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-ControlCondition $ControlType $Name))
}

function Invoke-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$Index = 0
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $allButtons = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $buttons = @($allButtons | Where-Object {
        [string]::Equals(
            $_.Current.Name.Replace("_", ""),
            $Name.Replace("_", ""),
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($buttons.Count -le $Index) {
        $available = @($allButtons | ForEach-Object {
            $_.Current.Name
        }) -join ", "
        throw "Required button '$Name' index $Index was not found. Available: $available"
    }
    $button = $buttons[$Index]
    if (-not $button.Current.IsEnabled) {
        throw "Required button '$Name' index $Index is disabled."
    }
    $pattern = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-EditValue {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [string]$Value
    )
    $edit = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Edit) $Name
    $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Select-Tab {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::TabItem)
    $tabs = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    $tab = $null
    foreach ($candidate in $tabs) {
        $normalized = $candidate.Current.Name.Replace("_", "")
        if ([string]::Equals(
                $normalized,
                $Name.Replace("_", ""),
                [StringComparison]::OrdinalIgnoreCase)) {
            $tab = $candidate
            break
        }
    }
    if ($null -eq $tab) {
        $names = @($tabs | ForEach-Object { $_.Current.Name }) -join ", "
        throw "Required tab '$Name' was not found. Available: $names"
    }
    $pattern = [System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Select-ComboItem {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$ComboName,
        [string]$ItemName
    )
    $combo = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::ComboBox) $ComboName
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        Wait-Until -Failure "Combo item '$ItemName' did not appear for '$ComboName'." -Condition {
            $named = New-ControlCondition `
                ([System.Windows.Automation.ControlType]::ListItem) $ItemName
            $owned = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                $combo.Current.ProcessId)
            $condition = New-Object System.Windows.Automation.AndCondition(
                $named, $owned)
            $script:comboItems = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $condition)
            return $script:comboItems.Count -gt 0
        }
        $selected = $false
        foreach ($candidate in $script:comboItems) {
            $patternObject = $null
            if ($candidate.TryGetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern,
                    [ref]$patternObject)) {
                ([System.Windows.Automation.SelectionItemPattern]$patternObject).Select()
                $selected = $true
                break
            }
        }
        if (-not $selected) {
            throw "Combo item '$ItemName' for '$ComboName' exposed no SelectionItem pattern."
        }
    }
    finally {
        try { $expand.Collapse() } catch { }
    }
}

function Get-ComboSelection {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $combo = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::ComboBox) $Name
    $pattern = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    $selection = $pattern.Current.GetSelection()
    if ($selection.Count -eq 0) { return "" }
    return $selection[0].Current.Name
}

function Set-ToggleState {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [bool]$Checked
    )
    $check = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $pattern = [System.Windows.Automation.TogglePattern]$check.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $wanted = if ($Checked) {
        [System.Windows.Automation.ToggleState]::On
    }
    else { [System.Windows.Automation.ToggleState]::Off }
    if ($pattern.Current.ToggleState -ne $wanted) { $pattern.Toggle() }
}

function Get-ToggleState {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $check = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $pattern = [System.Windows.Automation.TogglePattern]$check.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState
}

function Get-ListCount {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $list = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::List) $Name
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return $list.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition).Count
}

function Select-ListIndex {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$Index
    )
    $list = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::List) $Name
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $list.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
    if ($items.Count -le $Index) {
        throw "List '$Name' has no item index $Index."
    }
    $pattern = [System.Windows.Automation.SelectionItemPattern]$items[$Index].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
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

function Test-InformativeBitmap {
    param(
        [System.Drawing.Bitmap]$Bitmap
    )
    $colors = New-Object "System.Collections.Generic.HashSet[int]"
    for ($y = 40; $y -lt $Bitmap.Height; $y += 10) {
        for ($x = 10; $x -lt $Bitmap.Width; $x += 10) {
            [void]$colors.Add($Bitmap.GetPixel($x, $y).ToArgb())
            if ($colors.Count -ge 10) {
                return $true
            }
        }
    }
    return $false
}

function Scroll-VerticalToEnd {
    param(
        [System.Windows.Automation.AutomationElement]$Root
    )
    $elements = $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($element in $elements) {
        try {
            $pattern = [System.Windows.Automation.ScrollPattern]$element.GetCurrentPattern(
                [System.Windows.Automation.ScrollPattern]::Pattern)
            if ($pattern.Current.VerticallyScrollable) {
                $pattern.SetScrollPercent(
                    [System.Windows.Automation.ScrollPattern]::NoScroll,
                    100.0)
                return
            }
        }
        catch {
            # Most descendants do not expose ScrollPattern; keep looking.
        }
    }
    throw "The selected task exposed no vertically scrollable UI Automation surface."
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $rect = New-Object SkyGui022Native+Rect
    if (-not [SkyGui022Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not resolve visible window rectangle for $Handle."
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "Visible window $Handle has an invalid rectangle."
    }
    $lastFailure = $null
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $bitmap = New-Object System.Drawing.Bitmap($width, $height)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen(
                    $rect.Left, $rect.Top, 0, 0,
                    (New-Object System.Drawing.Size($width, $height)))
            }
            finally { $graphics.Dispose() }
            if (-not (Test-InformativeBitmap $bitmap)) {
                throw "The screen capture contained no informative client pixels."
            }
            $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
            return
        }
        catch {
            $lastFailure = $_
            if ($attempt -lt 5) {
                Start-Sleep -Milliseconds 300
            }
        }
        finally { $bitmap.Dispose() }
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $deviceContext = [IntPtr]::Zero
        try {
            $deviceContext = $graphics.GetHdc()
            if (-not [SkyGui022Native]::PrintWindow(
                    $Handle, $deviceContext, 0x00000002)) {
                throw "PrintWindow returned false."
            }
        }
        finally {
            if ($deviceContext -ne [IntPtr]::Zero) {
                $graphics.ReleaseHdc($deviceContext)
            }
            $graphics.Dispose()
        }
        if (-not (Test-InformativeBitmap $bitmap)) {
            throw "PrintWindow returned a blank WPF client frame."
        }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    catch {
        throw "Could not capture visible window $Handle with screen or PrintWindow paths. Screen failure: $lastFailure. PrintWindow failure: $_"
    }
    finally { $bitmap.Dispose() }
}

function Start-OptionsModal {
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
        'Open Skyrim CharGen options editor')))
`$button = `$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button) { throw 'CharGen editor button was not found.' }
`$pattern = [System.Windows.Automation.InvokePattern]`$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
`$pattern.Invoke()
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden -PassThru
    try {
        Wait-Until -Failure "The packaged CharGen modal did not become visible." -Condition {
            foreach ($row in [SkyGui022Native]::VisibleWindowsForProcess(
                    [uint32]$Application.Id)) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $MainHandle -and
                    $parts[1] -eq "Skyrim CharGen options") {
                    $script:acceptedModalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        $handle = $script:acceptedModalHandle
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        Wait-Until -Failure "The packaged CharGen modal content did not finish loading." -Condition {
            $control = $root.FindFirst(
                [System.Windows.Automation.TreeScope]::Descendants,
                (New-ControlCondition `
                    ([System.Windows.Automation.ControlType]::ComboBox) `
                    "Diffuse output resolution"))
            return $null -ne $control
        }
        return [pscustomobject]@{
            Handle = $handle
            Root = $root
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
    Wait-Until -Failure "The packaged CharGen modal did not close." -Condition {
        $prefix = $Handle.ToInt64().ToString() + "|"
        return -not ([SkyGui022Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id) |
            Where-Object { $_.StartsWith($prefix) })
    }
    if (-not $Helper.WaitForExit(3000)) {
        throw "The modal opener remained blocked after modal close."
    }
}

function Assert-NoProductionOutput {
    if (Test-Path -LiteralPath $proposalPath -PathType Leaf) {
        throw "A modal rollback path wrote the production proposal."
    }
    if (Test-Path -LiteralPath $outputRoot) {
        throw "A modal rollback path created the production output root."
    }
}

$application = $null
$activeHelpers = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $application = Start-Process -FilePath $executable -PassThru
    Wait-Until -Failure "The packaged desktop did not create a main window." `
        -TimeoutSeconds 20 -Condition {
            $application.Refresh()
            return $application.MainWindowHandle -ne 0
        }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)
    Select-Tab $mainRoot "Edit and consume Skyrim CharGen options task"

    Set-EditValue $mainRoot "CharGen source options path" $sourceOptions
    Set-EditValue $mainRoot "CharGen source options SHA-256" $sourceHash
    Set-EditValue $mainRoot "CharGen copied Data root" $dataRoot
    Set-EditValue $mainRoot "CharGen copied plugin order" "NativeTintFixture.esp"
    Set-EditValue $mainRoot "CharGen NPC reference" `
        "NativeTintFixture.esp|0x00000802"
    Set-EditValue $mainRoot "CharGen expected race reference" `
        "NativeTintFixture.esp|0x00000801"
    Set-EditValue $mainRoot "CharGen production proposal path" $proposalPath
    Set-EditValue $mainRoot "CharGen production output root" $outputRoot
    Invoke-Button $mainRoot "Review exact CharGen source"
    Wait-Until -Failure "The exact opening options source was not reviewed." -Condition {
        Test-TextContains $mainRoot "Source reviewed"
    }
    $checks.sourceHashReview = $true

    $session = Start-OptionsModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Set-ToggleState $session.Root "Generate TGA beside FaceGen DDS" $true
    Invoke-Button $session.Root "Cancel CharGen options"
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-NoProductionOutput
    $checks.cancelRollback = $true

    $session = Start-OptionsModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Set-ToggleState $session.Root "Generate TGA beside FaceGen DDS" $true
    [void][SkyGui022Native]::PostMessage(
        $session.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-NoProductionOutput
    $checks.titleCloseRollback = $true

    $session = Start-OptionsModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    Set-ToggleState $session.Root "Generate TGA beside FaceGen DDS" $true
    [void][SkyGui022Native]::PostMessage(
        $session.Handle, 0x0100, [IntPtr]0x1B, [IntPtr]::Zero)
    [void][SkyGui022Native]::PostMessage(
        $session.Handle, 0x0101, [IntPtr]0x1B, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Assert-NoProductionOutput
    $checks.escapeRollback = $true

    $session = Start-OptionsModal $application $mainHandle
    $activeHelpers.Add($session.Helper)
    $rect = New-Object SkyGui022Native+Rect
    [void][SkyGui022Native]::GetWindowRect($session.Handle, [ref]$rect)
    if (-not [SkyGui022Native]::MoveWindow(
            $session.Handle, $rect.Left, $rect.Top, 1160, 840, $true)) {
        throw "The sizable production modal refused a real resize."
    }
    Start-Sleep -Milliseconds 250
    $resized = New-Object SkyGui022Native+Rect
    [void][SkyGui022Native]::GetWindowRect($session.Handle, [ref]$resized)
    if (($resized.Right - $resized.Left) -lt 1150 -or
        ($resized.Bottom - $resized.Top) -lt 830) {
        throw "The production modal did not retain the requested resize."
    }
    $checks.resize = $true

    Select-Tab $session.Root "Texture output"
    Select-ComboItem $session.Root "Diffuse output resolution" "R1024"
    Set-ToggleState $session.Root `
        "Use per-layer Skyrim FaceGen texture settings" $true
    Set-ToggleState $session.Root `
        "Use per-layer Skyrim FaceGen texture settings" $false
    Invoke-Button $session.Root "Reset texture output to Skyrim defaults"
    Wait-Until -Failure "Texture reset did not restore Inherit." -Condition {
        (Get-ComboSelection $session.Root "Diffuse output resolution") -eq
            "Inherit"
    }
    Select-ComboItem $session.Root "Diffuse output resolution" "R512"
    Set-ToggleState $session.Root "Bake Skyrim RaceMenu face overlays" $false
    Set-ToggleState $session.Root "Generate TGA beside FaceGen DDS" $false
    Start-Sleep -Milliseconds 300
    Capture-Window $session.Handle $screenshots.texture
    $checks.textureModesAndReset = $true

    Select-Tab $session.Root "FaceTint conventions"
    Select-ComboItem $session.Root "Diffuse working space" "G22"
    Invoke-Button $session.Root `
        "Reset FaceTint conventions to Skyrim defaults"
    Wait-Until -Failure "Convention reset did not restore Linear." -Condition {
        (Get-ComboSelection $session.Root "Diffuse working space") -eq
            "Linear"
    }
    Start-Sleep -Milliseconds 300
    Capture-Window $session.Handle $screenshots.conventions
    $checks.conventionEditAndReset = $true

    Select-Tab $session.Root "Tint order"
    Invoke-Button $session.Root "Add" 0
    Wait-Until -Failure "Duplicate tint key did not expose validation." -Condition {
        Test-TextContains $session.Root "already present"
    }
    Capture-Window $session.Handle $screenshots.validation
    $checks.duplicateValidation = $true

    Select-ComboItem $session.Root "Tint rule key" "TintIndex"
    Invoke-Button $session.Root "Add" 0
    Wait-Until -Failure "Tint add did not create two rows." -Condition {
        (Get-ListCount $session.Root "Ordered Skyrim tint rules") -eq 2
    }
    Select-ListIndex $session.Root "Ordered Skyrim tint rules" 1
    Invoke-Button $session.Root "Move up" 0
    Invoke-Button $session.Root "Move down" 0
    Invoke-Button $session.Root "Remove" 0
    Select-ComboItem $session.Root "Overlay rule key" "Alpha"
    Invoke-Button $session.Root "Add" 1
    Wait-Until -Failure "Overlay add did not create two rows." -Condition {
        (Get-ListCount $session.Root "Ordered Skyrim overlay rules") -eq 2
    }
    Select-ListIndex $session.Root "Ordered Skyrim overlay rules" 1
    Invoke-Button $session.Root "Move up" 1
    Invoke-Button $session.Root "Move down" 1
    Invoke-Button $session.Root "Remove" 1
    Invoke-Button $session.Root `
        "Reset tint and overlay order to Skyrim defaults"
    if ((Get-ListCount $session.Root "Ordered Skyrim tint rules") -ne 1 -or
        (Get-ListCount $session.Root "Ordered Skyrim overlay rules") -ne 1) {
        throw "Ordering reset did not restore one rule per list."
    }
    Capture-Window $session.Handle $screenshots.order
    $checks.addRemoveMoveAndReset = $true

    Select-Tab $session.Root "Authority"
    Wait-Until -Failure "Authority tab did not expose runtime boundary." -Condition {
        Test-TextContains $session.Root "runtime rendering"
    }
    Capture-Window $session.Handle $screenshots.authority
    $checks.authorityTab = $true

    [void][SkyGui022Native]::PostMessage(
        $session.Handle, 0x0100, [IntPtr]0x0D, [IntPtr]::Zero)
    [void][SkyGui022Native]::PostMessage(
        $session.Handle, 0x0101, [IntPtr]0x0D, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    Wait-Until -Failure "Enter did not accept the complete modal document." -Condition {
        Test-TextContains $mainRoot "Options accepted"
    }
    $checks.enterSave = $true

    Invoke-Button $mainRoot "Review CharGen production proposal"
    Wait-Until -Failure "Proposal review did not retain JSON." -Condition {
        (Test-Path -LiteralPath $proposalPath -PathType Leaf) -and
        (Test-TextContains $mainRoot "Proposal reviewed")
    }
    if (Test-Path -LiteralPath $outputRoot) {
        throw "Review created the production output root before Apply."
    }
    Scroll-VerticalToEnd $mainRoot
    Start-Sleep -Milliseconds 300
    Capture-Window $mainHandle $screenshots.proposal
    $checks.proposalOnlyReview = $true

    Invoke-Button $mainRoot "Write bake and verify CharGen options"
    Wait-Until -Failure "Apply did not retain all three production outputs." `
        -TimeoutSeconds 30 -Condition {
            (Test-Path -LiteralPath `
                (Join-Path $outputRoot "accepted-options.json") -PathType Leaf) -and
            (Test-Path -LiteralPath `
                (Join-Path $outputRoot "facetint.dds") -PathType Leaf) -and
            (Test-Path -LiteralPath `
                (Join-Path $outputRoot "options-to-facetint-receipt.json") `
                -PathType Leaf)
        }
    Wait-Until -Failure "The final static/runtime verdict was not visible." -Condition {
        Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED"
    }
    Scroll-VerticalToEnd $mainRoot
    Start-Sleep -Milliseconds 300
    Capture-Window $mainHandle $screenshots.final
    $checks.writeReopenNativeBake = $true

    $proposalHash = (Get-FileHash -LiteralPath $proposalPath `
        -Algorithm SHA256).Hash
    $verifier = Join-Path $PSScriptRoot `
        "verify_sky_gui_022_chargen_options.ps1"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier `
        -ProposalPath $proposalPath `
        -ExpectedProposalSha256 $proposalHash `
        -OutputRoot $outputRoot `
        -ExpectedSourceOptionsSha256 $sourceHash `
        -ExpectedDataRoot $dataRoot `
        -ExpectedPlugin "NativeTintFixture.esp" `
        -ExpectedNpc "NativeTintFixture.esp|0x00000802" `
        -ExpectedRace "NativeTintFixture.esp|0x00000801" `
        -ExpectedDdsSha256 `
            "9674C776250D41485DBD55CC78F43692F104EAC1B93316BC6492650026225E86" `
        -ReportPath $rawReport | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Independent packaged artifact verification failed."
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier `
        -ProposalPath $proposalPath `
        -ExpectedProposalSha256 $proposalHash `
        -OutputRoot $outputRoot `
        -ExpectedSourceOptionsSha256 $sourceHash `
        -ExpectedDataRoot $dataRoot `
        -ExpectedPlugin "NativeTintFixture.esp" `
        -ExpectedNpc "NativeTintFixture.esp|0x00000802" `
        -ExpectedRace "NativeTintFixture.esp|0x00000801" `
        -ExpectedDdsSha256 ("0" * 64) `
        -ReportPath $negativeReport -ExpectFailure | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged wrong-DDS negative control did not fail as expected."
    }
    $raw = Get-Content -Raw -LiteralPath $rawReport | ConvertFrom-Json
    $negative = Get-Content -Raw -LiteralPath $negativeReport |
        ConvertFrom-Json
    if ($raw.verdict -ne "PASS" -or $raw.mismatchCount -ne 0 -or
        $negative.verdict -ne "EXPECTED_FAIL" -or
        $negative.mismatchCount -ne 1) {
        throw "Independent verification verdicts were not exact."
    }
    $checks.independentVerifierAndNegativeControl = $true

    $screenshotEvidence = foreach ($entry in $screenshots.GetEnumerator()) {
        $item = Get-Item -LiteralPath $entry.Value
        [ordered]@{
            state = $entry.Key
            path = $item.FullName.Substring($projectRoot.Length + 1).Replace("\", "/")
            size = $item.Length
            sha256 = (Get-FileHash -LiteralPath $item.FullName `
                -Algorithm SHA256).Hash
        }
    }
    $result = [ordered]@{
        schemaVersion = 1
        artifactKind = "sky-gui-022-packaged-acceptance"
        result = "PASS"
        verdict = "STATIC_PASS_RUNTIME_REQUIRED"
        surfaceId = "SKY-GUI-022"
        runTag = $RunTag
        packagedExecutable = $executable.Substring(
            $projectRoot.Length + 1).Replace("\", "/")
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable `
            -Algorithm SHA256).Hash
        sourceOptionsSha256 = $sourceHash
        proposalPath = $proposalPath.Substring(
            $projectRoot.Length + 1).Replace("\", "/")
        proposalSha256 = $proposalHash
        outputRoot = $outputRoot.Substring(
            $projectRoot.Length + 1).Replace("\", "/")
        acceptedOptionsSha256 = (Get-FileHash -LiteralPath `
            (Join-Path $outputRoot "accepted-options.json") `
            -Algorithm SHA256).Hash
        faceTintSha256 = (Get-FileHash -LiteralPath `
            (Join-Path $outputRoot "facetint.dds") `
            -Algorithm SHA256).Hash
        receiptSha256 = (Get-FileHash -LiteralPath `
            (Join-Path $outputRoot "options-to-facetint-receipt.json") `
            -Algorithm SHA256).Hash
        checks = $checks
        independentVerification = $rawReport.Substring(
            $projectRoot.Length + 1).Replace("\", "/")
        negativeControl = $negativeReport.Substring(
            $projectRoot.Length + 1).Replace("\", "/")
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
        protectedLiveRootTouched = $false
    }
    $json = $result | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText(
        $reportFull,
        $json + [Environment]::NewLine,
        (New-Object Text.UTF8Encoding($false)))
    Write-Output $json
}
finally {
    foreach ($helper in $activeHelpers) {
        if ($null -ne $helper -and -not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force
        }
    }
    if ($null -ne $application -and -not $application.HasExited) {
        [void][SkyGui022Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)
        if (-not $application.WaitForExit(4000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
