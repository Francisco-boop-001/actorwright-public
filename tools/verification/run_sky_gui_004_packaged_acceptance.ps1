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
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-004-acceptance-input-$RunTag"
$catalogRoot = Join-Path $inputRoot "catalog"
$dataRoot = Join-Path $inputRoot "Data"
$loadOrderPath = Join-Path $inputRoot "load-order.json"
$requestPath = Join-Path $inputRoot "execution-request.json"
$preflightOutput = Join-Path $inputRoot "future-preflight-output"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$sourceCatalog = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\fresh-export-drop"
$sourceData = Join-Path $labRoot "projects\Emi2RenderProbe\03-builds\v0.1-render-probe\ck-root\Data"
$sourceRequest = Join-Path $projectRoot "01-source-copies\gate2-emi2\execution-request-v5.json"
$sourceBundle = Join-Path $projectRoot "01-source-copies\gate2-emi2\bundle.json"
$selectedHash = "0C635A34EEAE81816444F78BD89088A318B215629435C0BB08890E3B0E6303EA"
$screenshots = [ordered]@{
    cancel = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-004-cancel.png"
    selected = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-004-selected.png"
    accepted = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-004-accepted.png"
    titleClose = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-004-title-close.png"
    verifiedOutput = Join-Path $screenshotRootFull "2026-07-22-SKY-GUI-004-verified-output.png"
}

foreach ($path in @($executable, $sourceRequest, $sourceBundle,
        (Join-Path $sourceCatalog "emi2-neutral.jslot"),
        (Join-Path $sourceCatalog "emi2-neutral.nif"),
        (Join-Path $sourceCatalog "emi2-neutral.dds"), $sourceData)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required acceptance input is absent: $path"
    }
}
if (Test-Path -LiteralPath $inputRoot) {
    throw "Acceptance input root exists and will not be overwritten: $inputRoot"
}
foreach ($path in $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence exists and will not be overwritten: $path"
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

function Copy-BoundedFile {
    param([string]$Source, [string]$Destination)
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $parent)
    }
    Copy-Item -LiteralPath $Source -Destination $Destination
}

[void](New-Item -ItemType Directory -Path $catalogRoot)
[void](New-Item -ItemType Directory -Path $dataRoot)

# Build a bounded copied Data closure. Only the vanilla texture archive is needed
# by the typed selection transaction; meshes and unrelated archives stay omitted.
foreach ($file in Get-ChildItem -LiteralPath $sourceData -Recurse -File) {
    if ($file.Extension -in @(".esp", ".esm", ".esl")) { continue }
    if ($file.Extension -eq ".bsa" -and
        $file.Name -ne "Skyrim - Textures0.bsa") { continue }
    $relative = $file.FullName.Substring($sourceData.Length + 1)
    Copy-BoundedFile $file.FullName (Join-Path $dataRoot $relative)
}
$plugins = [ordered]@{
    "Skyrim.esm" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\official-masters\Skyrim.esm"
    "Update.esm" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\official-masters\Update.esm"
    "Dawnguard.esm" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\official-masters\Dawnguard.esm"
    "High Poly Head.esm" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\High Poly Head.esm"
    "Improved Eyes Skyrim.esp" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\Improved Eyes Skyrim.esp"
    "Koralina's Eyebrows.esp" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\Koralina's Eyebrows.esp"
    "KS Hairdo's.esp" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\dependency-providers\KS Hairdo's.esp"
    "GoamElvenEars.esp" = Join-Path $labRoot "projects\Emi2FreshBuild\01-source-copies\provider-evidence\GoamElvenEars.esp"
}
$orderRows = New-Object System.Collections.Generic.List[object]
$order = 0
foreach ($entry in $plugins.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required copied provider is absent: $($entry.Value)"
    }
    Copy-BoundedFile $entry.Value (Join-Path $dataRoot $entry.Key)
    $orderRows.Add([ordered]@{ name = $entry.Key; order = $order; enabled = $true })
    $order++
}
$serializedOrderRows = @($orderRows | ForEach-Object { $_ })
$loadOrderJson = [ordered]@{
    schemaVersion = 1
    edition = "skyrimse"
    plugins = $serializedOrderRows
} | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($loadOrderPath, $loadOrderJson, $utf8NoBom)

foreach ($stem in @("emi2-prepared", "emi2-selected")) {
    Copy-Item -LiteralPath (Join-Path $sourceCatalog "emi2-neutral.jslot") `
        -Destination (Join-Path $catalogRoot "$stem.jslot")
    Copy-Item -LiteralPath (Join-Path $sourceCatalog "emi2-neutral.nif") `
        -Destination (Join-Path $catalogRoot "$stem.nif")
    Copy-Item -LiteralPath (Join-Path $sourceCatalog "emi2-neutral.dds") `
        -Destination (Join-Path $catalogRoot "$stem.dds")
}
[IO.File]::WriteAllText((Join-Path $catalogRoot "malformed-visible-omission.jslot"), '{"version":')

$preparedPreset = Join-Path $catalogRoot "emi2-prepared.jslot"
$preparedNif = Join-Path $catalogRoot "emi2-prepared.nif"
$preparedDds = Join-Path $catalogRoot "emi2-prepared.dds"
$selectedPreset = Join-Path $catalogRoot "emi2-selected.jslot"
$selectedNif = Join-Path $catalogRoot "emi2-selected.nif"
$selectedDds = Join-Path $catalogRoot "emi2-selected.dds"
foreach ($path in @($preparedPreset, $selectedPreset)) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $selectedHash) {
        throw "Real Emi preset hash drifted in the copied acceptance catalog: $path"
    }
}

$bundle = Get-Content -Raw -LiteralPath $sourceBundle | ConvertFrom-Json
$bundle.preset.path = Get-RelativeLabPath $preparedPreset
$bundle.charGen.faceGeomPath = Get-RelativeLabPath $preparedNif
$bundle.charGen.faceTintPath = Get-RelativeLabPath $preparedDds
$bundlePath = Join-Path $inputRoot "bundle.json"
$bundleJson = $bundle | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($bundlePath, $bundleJson, $utf8NoBom)
$bundleHash = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash

$request = Get-Content -Raw -LiteralPath $sourceRequest | ConvertFrom-Json
$request.presetBundle.manifestPath = Get-RelativeLabPath $bundlePath
$request.presetBundle.manifestSha256 = $bundleHash
$request.presetBundle.presetPath = Get-RelativeLabPath $preparedPreset
$request.presetBundle.faceGeomPath = Get-RelativeLabPath $preparedNif
$request.presetBundle.faceTintPath = Get-RelativeLabPath $preparedDds
$request.output.root = Get-RelativeLabPath (Join-Path $inputRoot "future-initial-package")
$requestJson = $request | ConvertTo-Json -Depth 30
[IO.File]::WriteAllText($requestPath, $requestJson, $utf8NoBom)
$requestHash = (Get-FileHash -LiteralPath $requestPath -Algorithm SHA256).Hash

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SkyGui004Native
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
    public static extern bool BitBlt(
        IntPtr destination, int destinationX, int destinationY, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);

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
        [int]$TimeoutSeconds = 20
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 125
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
    $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
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
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-ListItemContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Text
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($item in $items) {
        if ($item.Current.Name.IndexOf($Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $item
        }
    }
    throw "No visible list item contains '$Text'."
}

function Try-FindFirstListItem {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
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
    Wait-Until -Failure "Edit '$Name' did not retain its requested value." -Condition {
        $current = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $current.Current.Value -eq $Value
    }
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

function Set-Checked {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [bool]$Checked
    )
    $box = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    if (-not $box.Current.IsEnabled) { throw "Checkbox '$Name' is disabled." }
    $toggle = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $desired = if ($Checked) {
        [System.Windows.Automation.ToggleState]::On
    } else {
        [System.Windows.Automation.ToggleState]::Off
    }
    if ($toggle.Current.ToggleState -ne $desired) { $toggle.Toggle() }
    Wait-Until -Failure "Checkbox '$Name' did not reach $desired." -Condition {
        $current = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        $current.Current.ToggleState -eq $desired
    }
}

function Get-Checked {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $box = Find-Element $Root ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $toggle = [System.Windows.Automation.TogglePattern]$box.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    return $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Invoke-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $button = Find-Element $Root ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) { throw "Required button '$Name' is disabled." }
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

function Test-TextContains {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Needle
    )
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($text in $texts) {
        if ($text.Current.Name.IndexOf($Needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }
    return $false
}

function Get-TextSnapshot {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    $texts = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    return (($texts | ForEach-Object { $_.Current.Name } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique) -join " || ")
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Handle)
    try {
        $window = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern)
        $window.SetWindowVisualState(
            [System.Windows.Automation.WindowVisualState]::Normal)
    } catch {
        # Native restoration below remains authoritative when WindowPattern is unavailable.
    }
    $rect = New-Object SkyGui004Native+Rect
    $restored = $false
    foreach ($ignored in 1..20) {
        [void][SkyGui004Native]::ShowWindow($Handle, 9)
        [void][SkyGui004Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui004Native]::PostMessage(
            $Handle, 0x0112, [IntPtr]0xF120, [IntPtr]::Zero)
        [void][SkyGui004Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 150
        if ([SkyGui004Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui004Native]::IsIconic($Handle) -and
            ($rect.Right - $rect.Left) -ge 640 -and
            ($rect.Bottom - $rect.Top) -ge 400) {
            $restored = $true
            break
        }
    }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if (-not $restored) {
        throw "Screenshot target $Handle did not restore to a visible product-sized window ($width x $height)."
    }
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            $screenContext = [SkyGui004Native]::GetDC([IntPtr]::Zero)
            try {
                if ($screenContext -eq [IntPtr]::Zero -or
                    -not [SkyGui004Native]::BitBlt(
                        $deviceContext, 0, 0, $width, $height,
                        $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                    if (-not [SkyGui004Native]::PrintWindow($Handle, $deviceContext, 0)) {
                        throw "BitBlt and PrintWindow refused visible screenshot capture for $Handle."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui004Native]::ReleaseDC([IntPtr]::Zero, $screenContext)
                }
                $graphics.ReleaseHdc($deviceContext)
            }
        } finally { $graphics.Dispose() }
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}

function Start-OwnedModal {
    param(
        [System.Diagnostics.Process]$Application,
        [IntPtr]$MainHandle,
        [string]$ButtonName,
        [string]$WindowTitle,
        [int]$TimeoutSeconds = 30
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
        '$ButtonName')))
`$button = `$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'Required modal opener is absent or disabled: $ButtonName'
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
        $script:acceptedModalHandle = [IntPtr]::Zero
        Wait-Until -Failure "Owned modal '$WindowTitle' did not become visible." `
            -TimeoutSeconds $TimeoutSeconds -Condition {
            $rows = [SkyGui004Native]::VisibleWindowsForProcess([uint32]$Application.Id)
            foreach ($row in $rows) {
                $parts = $row -split "\|", 2
                $candidate = [IntPtr][long]$parts[0]
                if ($candidate -ne $MainHandle -and $parts[1] -eq $WindowTitle) {
                    $script:acceptedModalHandle = $candidate
                    return $true
                }
            }
            return $false
        }
        return [pscustomobject]@{
            Handle = $script:acceptedModalHandle
            Root = [System.Windows.Automation.AutomationElement]::FromHandle(
                $script:acceptedModalHandle)
            Helper = $helper
        }
    } catch {
        if (-not $helper.HasExited) { Stop-Process -Id $helper.Id -Force }
        throw
    }
}

function Wait-ModalClosed {
    param(
        [System.Diagnostics.Process]$Application,
        [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper,
        [int]$TimeoutSeconds = 30
    )
    Wait-Until -Failure "Owned modal did not close." -TimeoutSeconds $TimeoutSeconds -Condition {
        $rows = [SkyGui004Native]::VisibleWindowsForProcess([uint32]$Application.Id)
        return -not ($rows | Where-Object {
            $_.StartsWith($Handle.ToInt64().ToString() + "|")
        })
    }
    if (-not $Helper.WaitForExit(5000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Wait-CatalogLoaded {
    param([System.Windows.Automation.AutomationElement]$Root)
    Wait-Until -Failure "The real RaceMenu catalog did not finish loading." `
        -TimeoutSeconds 40 -Condition {
        (Test-TextContains $Root "2 preset(s) admitted") -and
        (Test-TextContains $Root "malformed-visible-omission.jslot")
    }
}

function Select-PresetRow {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Filter,
        [string]$Stem,
        [bool]$ApplyBodySlide
    )
    Set-EditValue $Root "Filter RaceMenu presets" $Filter
    $script:presetRow = $null
    Wait-Until -Failure "Preset row '$Stem' did not become visible." -Condition {
        if (-not (Test-TextContains $Root $Stem)) { return $false }
        $script:presetRow = Try-FindFirstListItem $Root
        return $null -ne $script:presetRow
    }
    Select-Item $script:presetRow
    Set-Checked $Root "Show only race-compatible presets" $true
    Set-Checked $Root "Apply BodySlide sliders" $ApplyBodySlide
    Wait-Until -Failure "Preset '$Stem' did not become hash-bound and acceptable." `
        -TimeoutSeconds 120 -Condition {
        $button = Find-Element $Root ([System.Windows.Automation.ControlType]::Button) `
            "Use selected RaceMenu preset"
        $button.Current.IsEnabled -and (Test-TextContains $Root $selectedHash) -and
            (Test-TextContains $Root "runtime authority false")
    }
}

$application = $null
$activeHelpers = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$checks = [ordered]@{}
try {
    $arguments = @(
        "--race-menu-request", $requestPath,
        "--race-menu-request-sha256", $requestHash)
    $application = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru
    Wait-Until -Failure "The packaged desktop did not create a main window." `
        -TimeoutSeconds 30 -Condition {
        $application.Refresh()
        $application.MainWindowHandle -ne 0
    }
    [void]$application.WaitForInputIdle(10000)
    $mainHandle = [IntPtr]$application.MainWindowHandle
    [void][SkyGui004Native]::ShowWindow($mainHandle, 9)
    [void][SkyGui004Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle($mainHandle)

    $script:openWorkspaceTab = $null
    Wait-Until -Failure "The packaged task shell did not finish rendering." -Condition {
        $script:openWorkspaceTab = Try-FindElement $mainRoot `
            ([System.Windows.Automation.ControlType]::TabItem) `
            "Open copied Skyrim workspace task"
        $null -ne $script:openWorkspaceTab
    }
    $openWorkspaceTab = $script:openWorkspaceTab
    Select-Item $openWorkspaceTab
    Wait-Until -Failure "The copied-workspace task did not finish rendering." -Condition {
        $null -ne (Try-FindElement $mainRoot `
            ([System.Windows.Automation.ControlType]::Edit) `
            "Copied Skyrim Data folder")
    }
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $dataRoot
    Set-EditValue $mainRoot "Explicit load-order manifest" $loadOrderPath
    Set-EditValue $mainRoot "Fresh workspace output folder" $preflightOutput
    try {
        Wait-Until -Failure "The copied workspace review did not enable." `
            -TimeoutSeconds 60 -Condition {
            (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::Button) `
                "Review copied Skyrim workspace").Current.IsEnabled
        }
    }
    catch {
        $values = @(
            Get-EditValue $mainRoot "Copied Skyrim Data folder",
            Get-EditValue $mainRoot "Explicit load-order manifest",
            Get-EditValue $mainRoot "Fresh workspace output folder") -join " | "
        $snapshot = Get-TextSnapshot $mainRoot
        throw "$($_.Exception.Message) Values: $values UI snapshot: $snapshot"
    }
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    $presetTab = Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
        "Create NPC from RaceMenu preset task"
    Wait-Until -Failure "The exact copied provider intake was not accepted." `
        -TimeoutSeconds 90 -Condition { $presetTab.Current.IsEnabled }
    Select-Item $presetTab
    Wait-Until -Failure "The prepared RaceMenu request and intake did not enable the catalog action." `
        -TimeoutSeconds 75 -Condition {
        $button = Try-FindElement $mainRoot ([System.Windows.Automation.ControlType]::Button) `
            "Choose from RaceMenu preset catalog"
        $null -ne $button -and $button.Current.IsEnabled
    }
    if (-not (Test-TextContains $mainRoot "emi2-prepared.jslot")) {
        throw "The enabled preset task did not display the exact prepared catalog source."
    }
    $checks.reviewedProviderIntake = $true

    $candidateRootsBefore = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "03-builds\work") `
        -Directory -Filter "racemenu-selection-*" | ForEach-Object FullName)

    $session = Start-OwnedModal $application $mainHandle `
        "Choose from RaceMenu preset catalog" "Choose a RaceMenu preset"
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    if (-not (Get-Checked $session.Root "Apply BodySlide sliders")) {
        throw "The prepared request did not open with BodySlide enabled."
    }
    Select-PresetRow $session.Root "selected" "emi2-selected" $false
    Capture-Window $session.Handle $screenshots.cancel
    Invoke-Button $session.Root "Cancel RaceMenu preset selection"
    Wait-ModalClosed $application $session.Handle $session.Helper
    if (-not (Test-TextContains $mainRoot "emi2-prepared.jslot")) {
        throw "Cancel changed the prepared caller request."
    }
    $checks.cancelRollback = $true

    $session = Start-OwnedModal $application $mainHandle `
        "Choose from RaceMenu preset catalog" "Choose a RaceMenu preset"
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    if (-not (Get-Checked $session.Root "Apply BodySlide sliders")) {
        throw "Cancel did not restore the prepared BodySlide choice."
    }
    Select-PresetRow $session.Root "selected" "emi2-selected" $false
    Capture-Window $session.Handle $screenshots.selected
    Invoke-Button $session.Root "Use selected RaceMenu preset"
    Wait-ModalClosed $application $session.Handle $session.Helper -TimeoutSeconds 180
    try {
        Wait-Until -Failure "The typed preset authority transaction did not commit." `
            -TimeoutSeconds 300 -Condition {
            (Test-TextContains $mainRoot "Selected preset committed after full authority readback") -and
            (Test-TextContains $mainRoot "emi2-selected.jslot")
        }
    }
    catch {
        $snapshot = Get-TextSnapshot $mainRoot
        throw "$($_.Exception.Message) UI snapshot: $snapshot"
    }
    Capture-Window $mainHandle $screenshots.accepted
    $checks.hashBoundCommit = $true
    $checks.bodySlideChoiceCommitted = $true

    $candidateRootsAfter = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot "03-builds\work") `
        -Directory -Filter "racemenu-selection-*" | ForEach-Object FullName)
    $newCandidateRoots = @($candidateRootsAfter | Where-Object {
        $candidateRootsBefore -notcontains $_
    })
    if ($newCandidateRoots.Count -ne 1) {
        throw "Expected one committed selection authority root, found $($newCandidateRoots.Count)."
    }
    $candidateRoot = $newCandidateRoots[0]
    $candidateBundlePath = Join-Path $candidateRoot "bundle.json"
    $candidateStandalonePath = Join-Path $candidateRoot "standalone-assets.json"
    $candidateRecordPath = Join-Path $candidateRoot "record-authority.json"
    foreach ($path in @($candidateBundlePath, $candidateStandalonePath, $candidateRecordPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Committed authority file is absent: $path"
        }
    }
    $candidateBundle = Get-Content -Raw -LiteralPath $candidateBundlePath | ConvertFrom-Json
    $candidateStandalone = Get-Content -Raw -LiteralPath $candidateStandalonePath | ConvertFrom-Json
    if ($candidateBundle.preset.path -ne (Get-RelativeLabPath $selectedPreset) -or
        $candidateBundle.preset.sha256 -ne $selectedHash -or
        $candidateStandalone.schemaVersion -ne 6) {
        throw "Independent candidate readback disagreed with the selected preset or schema-6 authority."
    }
    $checks.independentAuthorityReadback = $true

    $session = Start-OwnedModal $application $mainHandle `
        "Choose from RaceMenu preset catalog" "Choose a RaceMenu preset"
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    if (Get-Checked $session.Root "Apply BodySlide sliders") {
        throw "The committed BodySlide omission did not reach the next modal session."
    }
    Select-PresetRow $session.Root "prepared" "emi2-prepared" $true
    Capture-Window $session.Handle $screenshots.titleClose
    [void][SkyGui004Native]::PostMessage($session.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $session.Handle $session.Helper
    if (-not (Test-TextContains $mainRoot "emi2-selected.jslot")) {
        throw "Title-bar Close changed the accepted caller request."
    }
    $session = Start-OwnedModal $application $mainHandle `
        "Choose from RaceMenu preset catalog" "Choose a RaceMenu preset"
    $activeHelpers.Add($session.Helper)
    Wait-CatalogLoaded $session.Root
    if (Get-Checked $session.Root "Apply BodySlide sliders") {
        throw "Title-bar Close changed the accepted BodySlide omission."
    }
    Invoke-Button $session.Root "Cancel RaceMenu preset selection"
    Wait-ModalClosed $application $session.Handle $session.Helper
    $checks.titleCloseRollback = $true

    $packageRoot = Get-EditValue $mainRoot "Preset NPC fresh output folder"
    if (Test-Path -LiteralPath $packageRoot) {
        throw "The shared pipeline output was not fresh: $packageRoot"
    }
    $buildSession = Start-OwnedModal $application $mainHandle `
        "Create preset NPC package" "Build progress" 45
    $activeHelpers.Add($buildSession.Helper)
    Wait-ModalClosed $application $buildSession.Handle $buildSession.Helper -TimeoutSeconds 300
    try {
        Wait-Until -Failure "The shared pipeline did not retain a verified static package." `
            -TimeoutSeconds 60 -Condition {
            (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED") -and
            (Test-TextContains $mainRoot "No BodyGen files were emitted")
        }
    }
    catch {
        $snapshot = Get-TextSnapshot $mainRoot
        $outputState = if (Test-Path -LiteralPath $packageRoot) {
            "output exists"
        } else {
            "output absent"
        }
        throw "$($_.Exception.Message) $outputState at '$packageRoot'. UI snapshot: $snapshot"
    }
    [void][SkyGui004Native]::SetForegroundWindow($mainHandle)
    foreach ($ignored in 1..5) {
        [void][SkyGui004Native]::PostMessage(
            $mainHandle, 0x0100, [IntPtr]0x22, [IntPtr]::Zero)
        [void][SkyGui004Native]::PostMessage(
            $mainHandle, 0x0101, [IntPtr]0x22, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 90
    }
    Capture-Window $mainHandle $screenshots.verifiedOutput
    $checks.sharedPipelineCompleted = $true

    $packageManifest = Join-Path $packageRoot "npcmanager-package.json"
    if (-not (Test-Path -LiteralPath $packageManifest -PathType Leaf)) {
        throw "The retained package manifest is absent: $packageManifest"
    }
    $dotnet = Join-Path $labRoot "tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe"
    $cli = Join-Path $projectRoot "src\NpcManager.Cli\bin\Release\net10.0\npcm.dll"
    $verifyText = (& $dotnet $cli package verify --manifest $packageManifest --json 2>&1) -join "`n"
    if ($LASTEXITCODE -ne 0) {
        throw "Independent package verification failed: $verifyText"
    }
    $verifyJson = $verifyText | ConvertFrom-Json
    if (-not $verifyJson.verified) {
        throw "Independent package verification did not report success."
    }
    $checks.independentPackageVerification = $true

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
        surfaceId = "SKY-GUI-004"
        packagedExecutable = Get-RelativeLabPath $executable
        packagedExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        preparedRequest = Get-RelativeLabPath $requestPath
        preparedRequestSha256 = $requestHash
        copiedDataRoot = Get-RelativeLabPath $dataRoot
        loadOrder = Get-RelativeLabPath $loadOrderPath
        catalog = [ordered]@{
            root = Get-RelativeLabPath $catalogRoot
            admitted = 2
            malformedOmitted = 1
            selectedPath = Get-RelativeLabPath $selectedPreset
            selectedSha256 = $selectedHash
            selectedFaceGeomSha256 = (Get-FileHash -LiteralPath $selectedNif -Algorithm SHA256).Hash
            selectedFaceTintSha256 = (Get-FileHash -LiteralPath $selectedDds -Algorithm SHA256).Hash
        }
        committedAuthority = [ordered]@{
            root = Get-RelativeLabPath $candidateRoot
            bundleSha256 = (Get-FileHash -LiteralPath $candidateBundlePath -Algorithm SHA256).Hash
            recordSha256 = (Get-FileHash -LiteralPath $candidateRecordPath -Algorithm SHA256).Hash
            standaloneSha256 = (Get-FileHash -LiteralPath $candidateStandalonePath -Algorithm SHA256).Hash
            standaloneSchemaVersion = $candidateStandalone.schemaVersion
        }
        package = [ordered]@{
            root = Get-RelativeLabPath $packageRoot
            manifest = Get-RelativeLabPath $packageManifest
            manifestSha256 = (Get-FileHash -LiteralPath $packageManifest -Algorithm SHA256).Hash
            independentVerifySuccess = [bool]$verifyJson.verified
        }
        checks = $checks
        screenshots = @($screenshotEvidence)
        runtimeAuthority = $false
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
        [void][SkyGui004Native]::PostMessage(
            [IntPtr]$application.MainWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)
        if (-not $application.WaitForExit(5000)) {
            Stop-Process -Id $application.Id -Force
        }
    }
}
