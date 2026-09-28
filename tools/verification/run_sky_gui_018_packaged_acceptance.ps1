param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260723-1",
    [string]$ReportPath = "",
    [switch]$Gate019,
    [switch]$Gate020,
    [switch]$Gate021
)

$ErrorActionPreference = "Stop"
if (@(@($Gate019, $Gate020, $Gate021) | Where-Object { $_ }).Count -gt 1) {
    throw "Choose at most one Gate-019, Gate-020, or Gate-021 acceptance profile."
}
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
$pinnedDotnetRoot = Join-Path $labRoot `
    "tools\external\dotnet-sdk-10.0.301-win-x64"
if (-not (Test-Path -LiteralPath `
        (Join-Path $pinnedDotnetRoot "dotnet.exe"))) {
    throw "The pinned .NET 10 runtime root is absent: $pinnedDotnetRoot"
}
$env:DOTNET_ROOT = $pinnedDotnetRoot
$env:PATH = "$pinnedDotnetRoot;$env:PATH"
$isArmorAddonGate = $Gate019 -or $Gate020 -or $Gate021
$isCompleteAddonGate = $Gate020 -or $Gate021
$gateId = if ($Gate021) { "021" } elseif ($Gate020) { "020" } elseif ($Gate019) { "019" } else { "018" }
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot `
        "03-builds\work\sky-gui-$gateId-desktop-publish-20260723-1"
}
if ([string]::IsNullOrWhiteSpace($ScreenshotRoot)) {
    $ScreenshotRoot = Join-Path $projectRoot "Screenshots"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot `
        "05-reports\sky-gui-$gateId-packaged-acceptance-$RunTag.json"
}
$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$screenshotRootFull = (Resolve-Path -LiteralPath $ScreenshotRoot).Path
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$fixtureRoot = Join-Path $projectRoot `
    $(if ($isCompleteAddonGate) {
        "03-builds\work\sky-gui-020-fixture-20260723-2"
    } elseif ($Gate019) {
        "03-builds\work\sky-gui-019-fixture-20260723-1"
    } else {
        "03-builds\work\sky-gui-018-fixture-20260723-2"
    })
$inputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-$gateId-input-$RunTag"
$phase1Root = Join-Path $inputRoot "phase1"
$phase1Data = Join-Path $phase1Root "Data"
$phase1LoadOrder = Join-Path $phase1Root "load-order.json"
$phase1Preflight = Join-Path $phase1Root "future-output"
$phase2Root = Join-Path $inputRoot "phase2"
$phase2Data = Join-Path $phase2Root "Data"
$phase2LoadOrder = Join-Path $phase2Root "load-order.json"
$phase2Preflight = Join-Path $phase2Root "future-output"
$outputRoot = Join-Path $projectRoot "03-builds\work\sky-gui-$gateId-output-$RunTag"
$armorProposal = Join-Path $outputRoot "GeneratedArmor.armor-proposal.json"
$armorPlugin = Join-Path $outputRoot "GeneratedArmor.esp"
$armorAddonProposal = Join-Path $outputRoot `
    "GeneratedArmor.armor-proposal.Source.esp.000907.armor-addon-proposal.json"
$armorAddonPlugin = Join-Path $outputRoot "GeneratedAddonOverride.esp"
$cancelledOutfitProposal = Join-Path $outputRoot `
    "cancelled-authored-child.outfit-proposal.json"
$cancelledOutfitPlugin = Join-Path $outputRoot "CancelledAuthoredChild.esp"
$outfitProposal = Join-Path $outputRoot "generated-armor.outfit-proposal.json"
$outfitPlugin = Join-Path $outputRoot "GeneratedArmorOutfit.esp"
$rawVerifier = Join-Path $projectRoot `
    $(if ($Gate021) {
        "tools\verification\verify_sky_gui_021_mesh_picker.py"
    } elseif ($Gate020) {
        "tools\verification\verify_sky_gui_020_armor_addon.py"
    } elseif ($Gate019) {
        "tools\verification\verify_sky_gui_019_armor_addon_reference.py"
    } else {
        "tools\verification\verify_sky_gui_018_armor.py"
    })
$rawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-$gateId-armor-raw-audit-$RunTag.json"
$negativeRawReport = Join-Path $projectRoot `
    "05-reports\sky-gui-$gateId-negative-control-$RunTag.json"
$currentNpcScreenshot = Join-Path $labRoot `
    "Resources\Screenshot\NpcStudioEmiTrial.png"
$realBriarRoot = Join-Path $projectRoot `
    "01-source-copies\real-modlist\briar-armor-20260723\Data"
$realBriarArmaNif = Join-Path $realBriarRoot `
    "Meshes\armor\Briar\Briar_1.nif"
$realBriarFailureNif = Join-Path $realBriarRoot `
    "Meshes\armor\Briar\Briar_0.nif"
$realBriarWorldNif = Join-Path $realBriarRoot `
    "Meshes\armor\Briar\Briar_GND.nif"
$screenshots = [ordered]@{
    loaded = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-loaded-$RunTag.png"
    invalid = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-invalid-$RunTag.png"
    collections = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-collections-$RunTag.png"
    preview = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-preview-boundary-$RunTag.png"
    cancelledChild = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-cancelled-child-$RunTag.png"
    armorVerified = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-armor-verified-$RunTag.png"
    outfit = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-outfit-$RunTag.png"
    final = Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-final-$RunTag.png"
}
if ($isArmorAddonGate) {
    $screenshots.Add("chooser", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-chooser-$RunTag.png"))
    $screenshots.Add("chooserEmpty", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-chooser-empty-$RunTag.png"))
    $screenshots.Add("chooserReset", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-chooser-reset-$RunTag.png"))
    $screenshots.Add("deepEdit", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-deep-edit-$RunTag.png"))
    $screenshots.Add("deepEditOuter", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-deep-edit-outer-$RunTag.png"))
}
if ($isCompleteAddonGate) {
    $screenshots.Add("typedPicker", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-typed-picker-$RunTag.png"))
    $screenshots.Add("data", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-data-$RunTag.png"))
    $screenshots.Add("releaseAddon", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-$gateId-release-addon-$RunTag.png"))
}
if ($Gate021) {
    $screenshots.Add("meshEmpty", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-021-mesh-empty-$RunTag.png"))
    $screenshots.Add("meshFailure", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-021-mesh-preview-failure-$RunTag.png"))
    $screenshots.Add("meshSuccess", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-021-mesh-preview-success-$RunTag.png"))
    $screenshots.Add("meshPreselection", (Join-Path $screenshotRootFull `
        "2026-07-23-SKY-GUI-021-mesh-preselection-$RunTag.png"))
}

$required = @(
    $executable,
    (Join-Path $fixtureRoot "phase1\Data\OutfitBase.esm"),
    (Join-Path $fixtureRoot "phase1\Data\Source.esp"),
    (Join-Path $fixtureRoot "phase1\Data\OutfitProvider.esp"),
    (Join-Path $fixtureRoot "phase1\load-order.json"),
    (Join-Path $fixtureRoot "phase2\Data\OutfitBase.esm"),
    (Join-Path $fixtureRoot "phase2\Data\Source.esp"),
    (Join-Path $fixtureRoot "phase2\Data\OutfitProviderAfter.esp"),
    (Join-Path $fixtureRoot "phase2\load-order.json"),
    (Join-Path $fixtureRoot "expected.json"),
    $rawVerifier,
    $currentNpcScreenshot
)
if ($isArmorAddonGate) {
    $required += @(
        (Join-Path $fixtureRoot "phase1\Data\Provider.esp"),
        (Join-Path $fixtureRoot "phase2\Data\Provider.esp")
    )
}
if ($Gate021) {
    $required += @(
        $realBriarArmaNif,
        $realBriarFailureNif,
        $realBriarWorldNif,
        (Join-Path $realBriarRoot "Textures\armor\Briar\Briar.dds"),
        (Join-Path $realBriarRoot "Textures\armor\Briar\Briar_m.dds"),
        (Join-Path $realBriarRoot "Textures\armor\Briar\Briar_n.dds")
    )
}
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
foreach ($path in @($reportPathFull, $rawReport, $negativeRawReport) +
        $screenshots.Values) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance evidence exists and will not be overwritten: $path"
    }
}
if (-not $reportPathFull.StartsWith(
        $projectRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Acceptance report escaped the project root."
}

New-Item -ItemType Directory -Path $phase1Data | Out-Null
New-Item -ItemType Directory -Path $phase2Data | Out-Null
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$phase1Names = @("OutfitBase.esm", "Source.esp", "OutfitProvider.esp")
if ($isArmorAddonGate) { $phase1Names += "Provider.esp" }
foreach ($name in $phase1Names) {
    Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase1\Data\$name") `
        -Destination (Join-Path $phase1Data $name)
}
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase1\load-order.json") `
    -Destination $phase1LoadOrder
$phase2Names = @("OutfitBase.esm", "Source.esp", "OutfitProviderAfter.esp")
if ($isArmorAddonGate) { $phase2Names += "Provider.esp" }
foreach ($name in $phase2Names) {
    Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase2\Data\$name") `
        -Destination (Join-Path $phase2Data $name)
}
Copy-Item -LiteralPath (Join-Path $fixtureRoot "phase2\load-order.json") `
    -Destination $phase2LoadOrder
if ($Gate021) {
    foreach ($dataRoot in @($phase1Data, $phase2Data)) {
        Copy-Item -LiteralPath (Join-Path $realBriarRoot "Meshes") `
            -Destination $dataRoot -Recurse
        Copy-Item -LiteralPath (Join-Path $realBriarRoot "Textures") `
            -Destination $dataRoot -Recurse
    }
}
if ($isCompleteAddonGate) {
    $phase2Manifest = Get-Content -LiteralPath $phase2LoadOrder -Raw |
        ConvertFrom-Json
    foreach ($plugin in $phase2Manifest.plugins) {
        if ($plugin.order -ge 3) { $plugin.order += 1 }
    }
    $beforeGenerated = @($phase2Manifest.plugins |
        Where-Object { $_.order -lt 4 })
    $afterGenerated = @($phase2Manifest.plugins |
        Where-Object { $_.order -ge 4 })
    $phase2Manifest.plugins = @($beforeGenerated) + @(
        [pscustomobject][ordered]@{
            name = "GeneratedAddonOverride.esp"
            order = 3
            enabled = $true
        }) + @($afterGenerated)
    [IO.File]::WriteAllText(
        $phase2LoadOrder,
        ($phase2Manifest | ConvertTo-Json -Depth 8) + "`n",
        [Text.UTF8Encoding]::new($false))
}
$expected = Get-Content -LiteralPath (Join-Path $fixtureRoot "expected.json") `
    -Raw | ConvertFrom-Json
$hashChecks = @(
    @((Join-Path $phase1Data "OutfitBase.esm"), $expected.phase1BaseSha256),
    @((Join-Path $phase1Data "Source.esp"), $expected.phase1SourceSha256),
    @((Join-Path $phase1Data "OutfitProvider.esp"), $expected.phase1ProviderSha256),
    @((Join-Path $phase2Data "OutfitBase.esm"), $expected.phase2BaseSha256),
    @((Join-Path $phase2Data "Source.esp"), $expected.phase2SourceSha256),
    @((Join-Path $phase2Data "OutfitProviderAfter.esp"),
        $expected.phase2ProviderSha256)
)
if ($isArmorAddonGate) {
    $hashChecks += ,@(
        (Join-Path $phase1Data "Provider.esp"),
        $expected.phase1ArmorAddonProviderSha256)
    $hashChecks += ,@(
        (Join-Path $phase2Data "Provider.esp"),
        $expected.phase2ArmorAddonProviderSha256)
}
if ($Gate021) {
    foreach ($dataRoot in @($phase1Data, $phase2Data)) {
        $realMeshChecks = @(
            @((Join-Path $dataRoot "Meshes\armor\Briar\Briar_0.nif"),
                "953F85AA2D30559E5538687D37E9503E81B7A7967141A927BA00C2D28B6938F1"),
            @((Join-Path $dataRoot "Meshes\armor\Briar\Briar_1.nif"),
                "19DCFE2DC1545F823E2DC5DF2A44571568FE27FF8FFFF2899215C34C77AF4C88"),
            @((Join-Path $dataRoot "Meshes\armor\Briar\Briar_GND.nif"),
                "5C5FE6D84BA76F8BD82DF7AC1DEDEC07190E62EEDB5606F8A8FF47854D56AD9D")
        )
        foreach ($pair in $realMeshChecks) {
            if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
                $pair[1]) {
                throw "Copied real Briar mesh hash mismatch: $($pair[0])"
            }
        }
    }
}
foreach ($pair in $hashChecks) {
    if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
        $pair[1].ToUpperInvariant()) {
        throw "Copied Armor fixture hash mismatch: $($pair[0])"
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

public static class SkyGui018Native
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
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint flags);
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr destination, int destinationX,
        int destinationY, int width, int height, IntPtr source, int sourceX,
        int sourceY, uint operation);
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
    if ($null -eq $Root) {
        $stack = (Get-PSCallStack | ForEach-Object {
            "$($_.FunctionName):$($_.ScriptLineNumber)"
        }) -join " <- "
        throw "Automation root is null while finding '$Name'. Stack: $stack"
    }
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
    $button = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) {
        throw "Required button '$Name' is disabled."
    }
    ([System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Press-Button {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [IntPtr]$WindowHandle)
    $button = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Button) $Name
    if (-not $button.Current.IsEnabled) {
        throw "Required button '$Name' is disabled."
    }
    [void][SkyGui018Native]::ShowWindow($WindowHandle, 9)
    [void][SkyGui018Native]::SetForegroundWindow($WindowHandle)
    $button.SetFocus()
    Start-Sleep -Milliseconds 150
    $point = $button.GetClickablePoint()
    if (-not [SkyGui018Native]::SetCursorPos(
            [int][Math]::Round($point.X), [int][Math]::Round($point.Y))) {
        throw "Could not move to the clickable point for '$Name'."
    }
    [SkyGui018Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [SkyGui018Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 250
}

function Select-Tab {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $tab = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::TabItem) $Name
    ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Wait-Until -Failure "Tab '$Name' did not become selected." -Condition {
        ([System.Windows.Automation.SelectionItemPattern]$tab.GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected
    }
    Start-Sleep -Milliseconds 150
}

function Set-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [string]$Value)
    foreach ($attempt in 1..8) {
        $edit = Find-Element $Root `
            ([System.Windows.Automation.ControlType]::Edit) $Name
        $pattern = [System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue($Value)
        Start-Sleep -Milliseconds 150
        $live = Find-Element $Root `
            ([System.Windows.Automation.ControlType]::Edit) $Name
        $current = ([System.Windows.Automation.ValuePattern]$live.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
        if ($current -eq $Value) { return }
    }
    throw "Edit '$Name' did not retain the requested value."
}

function Type-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [string]$Value, [IntPtr]$WindowHandle)
    $edit = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Edit) $Name
    [void][SkyGui018Native]::ShowWindow($WindowHandle, 9)
    [void][SkyGui018Native]::SetForegroundWindow($WindowHandle)
    $edit.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    [System.Windows.Forms.SendKeys]::SendWait($Value)
    Wait-Until -Failure "Edit '$Name' did not retain typed input." -Condition {
        (Get-EditValue $Root $Name) -eq $Value
    }
}

function Get-EditValue {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    $edit = Find-Element $Root ([System.Windows.Automation.ControlType]::Edit) $Name
    return ([System.Windows.Automation.ValuePattern]$edit.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}

function Set-ToggleState {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$Name, [bool]$Checked)
    $element = Find-Element $Root `
        ([System.Windows.Automation.ControlType]::CheckBox) $Name
    $pattern = [System.Windows.Automation.TogglePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $expected = if ($Checked) {
        [System.Windows.Automation.ToggleState]::On
    } else {
        [System.Windows.Automation.ToggleState]::Off
    }
    foreach ($attempt in 1..3) {
        if ($pattern.Current.ToggleState -eq $expected) { return }
        $pattern.Toggle()
        Start-Sleep -Milliseconds 150
    }
    if ($pattern.Current.ToggleState -ne $expected) {
        throw "Checkbox '$Name' did not retain the requested state."
    }
}

function Test-ButtonEnabled {
    param([System.Windows.Automation.AutomationElement]$Root, [string]$Name)
    return (Find-Element $Root `
        ([System.Windows.Automation.ControlType]::Button) $Name).Current.IsEnabled
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

function Get-TextValues {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)
    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition) |
        ForEach-Object { $_.Current.Name })
}

function Find-ItemContaining {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [string]$Needle)
    $containers = @()
    foreach ($type in @([System.Windows.Automation.ControlType]::List,
                         [System.Windows.Automation.ControlType]::DataGrid)) {
        $candidate = Try-FindElement $Root $type $ContainerName
        if ($null -ne $candidate) { $containers += $candidate }
    }
    if ($containers.Count -ne 1) {
        throw "Container '$ContainerName' was absent or ambiguous."
    }
    foreach ($item in $containers[0].FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if (($item.Current.ControlType -eq
                [System.Windows.Automation.ControlType]::ListItem -or
             $item.Current.ControlType -eq
                [System.Windows.Automation.ControlType]::DataItem) -and
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

function Test-ItemContaining {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [string]$Needle)
    try {
        [void](Find-ItemContaining $Root $ContainerName $Needle)
        return $true
    }
    catch { return $false }
}

function Get-ListItems {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName)
    $containers = @()
    foreach ($type in @([System.Windows.Automation.ControlType]::List,
                         [System.Windows.Automation.ControlType]::DataGrid)) {
        $candidate = Try-FindElement $Root $type $ContainerName
        if ($null -ne $candidate) { $containers += $candidate }
    }
    if ($containers.Count -ne 1) {
        throw "Container '$ContainerName' was absent or ambiguous."
    }
    $condition = New-Object System.Windows.Automation.OrCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem)))
    return @($containers[0].FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $condition))
}

function Select-ListItemAt {
    param([System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName, [int]$Index)
    $items = @(Get-ListItems $Root $ContainerName)
    if ($Index -lt 0 -or $Index -ge $items.Count) {
        throw "List '$ContainerName' has $($items.Count) rows; index $Index is unavailable."
    }
    ([System.Windows.Automation.SelectionItemPattern]$items[$Index].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 150
}

function Capture-Window {
    param([IntPtr]$Handle, [string]$Path)
    $rect = New-Object SkyGui018Native+Rect
    foreach ($ignored in 1..20) {
        [void][SkyGui018Native]::ShowWindow($Handle, 9)
        [void][SkyGui018Native]::ShowWindowAsync($Handle, 9)
        [void][SkyGui018Native]::SetForegroundWindow($Handle)
        Start-Sleep -Milliseconds 120
        if ([SkyGui018Native]::GetWindowRect($Handle, [ref]$rect) -and
            -not [SkyGui018Native]::IsIconic($Handle) -and
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
            $screenContext = [SkyGui018Native]::GetDC([IntPtr]::Zero)
            try {
                if (-not [SkyGui018Native]::PrintWindow(
                        $Handle, $deviceContext, 2)) {
                    if ($screenContext -eq [IntPtr]::Zero -or
                        -not [SkyGui018Native]::BitBlt(
                            $deviceContext, 0, 0, $width, $height,
                            $screenContext, $rect.Left, $rect.Top, 0x00CC0020)) {
                        throw "BitBlt and PrintWindow refused screenshot capture."
                    }
                }
            }
            finally {
                if ($screenContext -ne [IntPtr]::Zero) {
                    [void][SkyGui018Native]::ReleaseDC(
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

function Resize-Window {
    param([IntPtr]$Handle, [int]$Width, [int]$Height)
    $rect = New-Object SkyGui018Native+Rect
    if (-not [SkyGui018Native]::GetWindowRect($Handle, [ref]$rect)) {
        throw "Could not read the modal bounds before resize."
    }
    if (-not [SkyGui018Native]::MoveWindow(
            $Handle, $rect.Left, $rect.Top, $Width, $Height, $true)) {
        throw "The packaged modal refused a user-size operation."
    }
    Wait-Until -Failure "The packaged modal did not retain its resized bounds." `
        -Condition {
        $current = New-Object SkyGui018Native+Rect
        [SkyGui018Native]::GetWindowRect($Handle, [ref]$current) -and
            ($current.Right - $current.Left) -eq $Width -and
            ($current.Bottom - $current.Top) -eq $Height
    }
}

function Wait-ProcessWindowByTitle {
    param([System.Diagnostics.Process]$Application, [IntPtr[]]$ExcludedHandles,
        [string]$TitleNeedle, [string]$Failure)
    $script:foundModal = $null
    Wait-Until -Failure $Failure -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui018Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            if ($ExcludedHandles -contains $candidate -or
                $parts[1].IndexOf($TitleNeedle,
                    [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            $automationRoot =
                [System.Windows.Automation.AutomationElement]::FromHandle(
                    $candidate)
            if ($null -eq $automationRoot) { continue }
            $script:foundModal = [pscustomobject]@{
                Handle = $candidate
                Root = $automationRoot
            }
            return $true
        }
        return $false
    }
    return $script:foundModal
}

function Open-ModalFromButton {
    param([System.Diagnostics.Process]$Application, [IntPtr]$OwnerHandle,
        [string]$ButtonName, [string]$TitleNeedle, [IntPtr[]]$ExcludedHandles)
    $helperError = Join-Path $outputRoot `
        ("modal-helper-" + [Guid]::NewGuid().ToString("N") + ".error.log")
    $escapedError = $helperError.Replace("'", "''")
    $escapedButton = $ButtonName.Replace("'", "''")
    $helperCode = @"
`$ErrorActionPreference = 'Stop'
try {
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
`$root = [System.Windows.Automation.AutomationElement]::FromHandle(
    [IntPtr]$($OwnerHandle.ToInt64()))
`$condition = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        '$escapedButton')))
`$button = `$root.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants, `$condition)
if (`$null -eq `$button -or -not `$button.Current.IsEnabled) {
    throw 'The requested modal opener is absent or disabled.'
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
    $modal = Wait-ProcessWindowByTitle $Application $ExcludedHandles `
        $TitleNeedle "The '$TitleNeedle' modal did not become visible."
    if ($null -eq $modal) {
        $detail = if (Test-Path -LiteralPath $helperError) {
            Get-Content -LiteralPath $helperError -Raw
        } else { "helper exit=$($helper.ExitCode)" }
        throw "The modal helper failed: $detail"
    }
    return [pscustomobject]@{
        Handle = $modal.Handle
        Root = $modal.Root
        Helper = $helper
    }
}

function Wait-ModalClosed {
    param([System.Diagnostics.Process]$Application, [IntPtr]$Handle,
        [System.Diagnostics.Process]$Helper)
    Wait-Until -Failure "The modal did not close." -TimeoutSeconds 45 -Condition {
        -not ([SkyGui018Native]::VisibleWindowsForProcess(
                [uint32]$Application.Id) |
            Where-Object { $_.StartsWith($Handle.ToInt64().ToString() + "|") })
    }
    if (-not $Helper.WaitForExit(10000)) {
        throw "The modal opener remained blocked after close."
    }
}

function Select-MeshCandidateAndWait {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$RelativePath,
        [bool]$ExpectPreviewImage
    )
    $script:meshCandidate = $null
    try {
        Wait-Until -Failure "Mesh candidate '$RelativePath' did not appear." `
            -Condition {
            $script:meshCandidate =
                Try-FindMeshItemContaining $Root $RelativePath
            $null -ne $script:meshCandidate
        }
    }
    catch {
        $list = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::List) `
            "Reviewed Skyrim NIF mesh catalog"
        if ($null -eq $list) {
            $list = Try-FindElement $Root `
                ([System.Windows.Automation.ControlType]::DataGrid) `
                "Reviewed Skyrim NIF mesh catalog"
        }
        $observed = if ($null -eq $list) {
            "<list absent>"
        } else {
            @($list.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition) |
                ForEach-Object {
                    "$($_.Current.ControlType.ProgrammaticName)='$($_.Current.Name)'"
                }) -join " | "
        }
        throw "Mesh candidate '$RelativePath' did not appear. Observed UIA: $observed"
    }
    ([System.Windows.Automation.SelectionItemPattern]$script:meshCandidate.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Wait-Until -Failure "Mesh preview did not settle for '$RelativePath'." `
        -TimeoutSeconds 240 -Condition {
        Test-ButtonEnabled $Root "Use selected Skyrim mesh"
    }
    $image = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::Image) `
        "Off-engine selected NIF geometry preview"
    if ($ExpectPreviewImage -and $null -eq $image) {
        throw "The real copied mesh '$RelativePath' produced no visible off-engine preview image."
    }
    if (-not $ExpectPreviewImage -and $null -ne $image -and
        -not $image.Current.IsOffscreen) {
        throw "The deliberately unavailable mesh '$RelativePath' unexpectedly retained a preview image."
    }
}

function Try-FindMeshItemContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Needle
    )
    $list = Try-FindElement $Root `
        ([System.Windows.Automation.ControlType]::List) `
        "Reviewed Skyrim NIF mesh catalog"
    if ($null -eq $list) {
        $list = Try-FindElement $Root `
            ([System.Windows.Automation.ControlType]::DataGrid) `
            "Reviewed Skyrim NIF mesh catalog"
    }
    if ($null -eq $list) { return $null }
    foreach ($item in $list.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($item.Current.ControlType -ne
                [System.Windows.Automation.ControlType]::ListItem -and
            $item.Current.ControlType -ne
                [System.Windows.Automation.ControlType]::DataItem) { continue }
        $names = @($item.Current.Name)
        $names += @($item.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition) |
            ForEach-Object { $_.Current.Name })
        if (@($names | Where-Object {
                    $_.IndexOf($Needle,
                        [StringComparison]::OrdinalIgnoreCase) -ge 0
                }).Count -gt 0) {
            return $item
        }
    }
    return $null
}

function Find-MeshItemContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Needle
    )
    $item = Try-FindMeshItemContaining $Root $Needle
    if ($null -eq $item) {
        throw "Mesh item containing '$Needle' was not found."
    }
    return $item
}

function DoubleClick-ItemContaining {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$ContainerName,
        [string]$Needle
    )
    $item = if ($ContainerName -eq "Reviewed Skyrim NIF mesh catalog") {
        Find-MeshItemContaining $Root $Needle
    } else {
        Find-ItemContaining $Root $ContainerName $Needle
    }
    $item.SetFocus()
    $point = $item.GetClickablePoint()
    if (-not [SkyGui018Native]::SetCursorPos(
            [int][Math]::Round($point.X), [int][Math]::Round($point.Y))) {
        throw "Could not move to mesh row '$Needle'."
    }
    foreach ($click in 1..2) {
        [SkyGui018Native]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [SkyGui018Native]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 80
    }
}

function Exercise-Gate021ArmorAddonMeshPickers {
    param(
        [System.Diagnostics.Process]$Application,
        [System.Windows.Automation.AutomationElement]$Root,
        [IntPtr]$Handle,
        [IntPtr[]]$ParentHandles,
        [switch]$ExerciseFailurePaths
    )
    $excluded = [IntPtr[]](@($ParentHandles) + @($Handle))
    $bodyPath = "armor/Briar/Briar_1.nif"
    $failurePath = "armor/Briar/Briar_0.nif"
    $expectedBody = "armor\Briar\Briar_1.nif"
    $sourceMale = Get-EditValue $Root "Male third-person model path"

    if ($ExerciseFailurePaths) {
        $missing = [IO.Path]::GetFullPath((Join-Path $phase1Data `
            "Meshes\armor\Briar\Briar_0.nif"))
        $phaseRoot = [IO.Path]::GetFullPath($phase1Data) + "\"
        if (-not $missing.StartsWith(
                $phaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The mesh failure control escaped the fresh phase-one Data root."
        }
        $holding = $missing + ".gate021-preview-missing"
        if (-not (Test-Path -LiteralPath $missing) -or
            (Test-Path -LiteralPath $holding)) {
            throw "The fresh mesh failure control is not in its expected state."
        }
        Move-Item -LiteralPath $missing -Destination $holding
        try {
            $failureModal = Open-ModalFromButton $Application $Handle `
                "Browse male third-person model" "Select Skyrim mesh" `
                $excluded
            Resize-Window $failureModal.Handle 1180 900
            Type-EditValue $failureModal.Root "Filter Skyrim mesh paths" `
                "definitely-not-present" $failureModal.Handle
            Wait-Until -Failure "The mesh picker did not expose an empty filter result." `
                -Condition {
                @(Get-ListItems $failureModal.Root `
                    "Reviewed Skyrim NIF mesh catalog").Count -eq 0
            }
            Capture-Window $failureModal.Handle $screenshots.meshEmpty
            Type-EditValue $failureModal.Root "Filter Skyrim mesh paths" `
                "Briar_0" $failureModal.Handle
            Select-MeshCandidateAndWait $failureModal.Root $failurePath $false
            Capture-Window $failureModal.Handle $screenshots.meshFailure
            Press-Button $failureModal.Root "Cancel mesh selection" `
                $failureModal.Handle
            Wait-ModalClosed $Application $failureModal.Handle `
                $failureModal.Helper
        }
        finally {
            if (Test-Path -LiteralPath $holding) {
                Move-Item -LiteralPath $holding -Destination $missing
            }
        }
        if ((Get-EditValue $Root "Male third-person model path") -ne
            $sourceMale) {
            throw "Mesh preview failure plus Cancel mutated the ARMA model field."
        }
    }

    $male = Open-ModalFromButton $Application $Handle `
        "Browse male third-person model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $male.Root $bodyPath $true
    if ($ExerciseFailurePaths) {
        Capture-Window $male.Handle $screenshots.meshSuccess
    }
    Press-Button $male.Root "Use selected Skyrim mesh" $male.Handle
    Wait-ModalClosed $Application $male.Handle $male.Helper
    $actualMale = Get-EditValue $Root "Male third-person model path"
    if ($actualMale -ne $expectedBody) {
        throw "The male third-person picker expected '$expectedBody' but stored '$actualMale'."
    }

    if ($ExerciseFailurePaths) {
        $preselected = Open-ModalFromButton $Application $Handle `
            "Browse male third-person model" "Select Skyrim mesh" $excluded
        $selectedRow = Find-MeshItemContaining $preselected.Root $bodyPath
        Wait-Until -Failure "The accepted real mesh was not exactly preselected." `
            -TimeoutSeconds 240 -Condition {
            ([System.Windows.Automation.SelectionItemPattern]$selectedRow.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected -and
            (Test-ButtonEnabled $preselected.Root "Use selected Skyrim mesh")
        }
        Capture-Window $preselected.Handle $screenshots.meshPreselection
        [void][SkyGui018Native]::PostMessage(
            $preselected.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-ModalClosed $Application $preselected.Handle $preselected.Helper
        if ((Get-EditValue $Root "Male third-person model path") -ne
            $expectedBody) {
            throw "Mesh-picker title close changed the preselected ARMA model."
        }
    }

    $female = Open-ModalFromButton $Application $Handle `
        "Browse female third-person model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $female.Root $bodyPath $true
    $femaleRow = Find-MeshItemContaining $female.Root $bodyPath
    [void][SkyGui018Native]::SetForegroundWindow($female.Handle)
    $femaleRow.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $Application $female.Handle $female.Helper

    $maleFirst = Open-ModalFromButton $Application $Handle `
        "Browse male first-person model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $maleFirst.Root $bodyPath $true
    DoubleClick-ItemContaining $maleFirst.Root `
        "Reviewed Skyrim NIF mesh catalog" $bodyPath
    Wait-ModalClosed $Application $maleFirst.Handle $maleFirst.Helper

    if ($ExerciseFailurePaths) {
        $femaleFirstBefore = Get-EditValue $Root "Female first-person model path"
        $femaleFirstCancel = Open-ModalFromButton $Application $Handle `
            "Browse female first-person model" "Select Skyrim mesh" $excluded
        Select-MeshCandidateAndWait $femaleFirstCancel.Root $bodyPath $true
        Press-Button $femaleFirstCancel.Root "Cancel mesh selection" `
            $femaleFirstCancel.Handle
        Wait-ModalClosed $Application $femaleFirstCancel.Handle `
            $femaleFirstCancel.Helper
        if ((Get-EditValue $Root "Female first-person model path") -ne
            $femaleFirstBefore) {
            throw "Female first-person mesh Cancel mutated the field."
        }
    }
    $femaleFirst = Open-ModalFromButton $Application $Handle `
        "Browse female first-person model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $femaleFirst.Root $bodyPath $true
    Press-Button $femaleFirst.Root "Use selected Skyrim mesh" $femaleFirst.Handle
    Wait-ModalClosed $Application $femaleFirst.Handle $femaleFirst.Helper

    foreach ($field in @(
            "Male third-person model path",
            "Female third-person model path",
            "Male first-person model path",
            "Female first-person model path")) {
        if ((Get-EditValue $Root $field) -ne $expectedBody) {
            throw "The Gate-021 ARMA caller '$field' lost its accepted real Briar path."
        }
    }
}

function Exercise-Gate021ArmorWorldMeshPickers {
    param(
        [System.Diagnostics.Process]$Application,
        [System.Windows.Automation.AutomationElement]$Root,
        [IntPtr]$Handle,
        [IntPtr[]]$ParentHandles
    )
    $excluded = [IntPtr[]](@($ParentHandles) + @($Handle))
    $relativePath = "armor/Briar/Briar_GND.nif"
    $expectedPath = "armor\Briar\Briar_GND.nif"
    $maleBefore = Get-EditValue $Root `
        "Male world model path relative to Meshes"
    $cancel = Open-ModalFromButton $Application $Handle `
        "Browse male world model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $cancel.Root $relativePath $true
    Press-Button $cancel.Root "Cancel mesh selection" $cancel.Handle
    Wait-ModalClosed $Application $cancel.Handle $cancel.Helper
    if ((Get-EditValue $Root `
            "Male world model path relative to Meshes") -ne $maleBefore) {
        throw "Male ARMO world-model Cancel mutated the field."
    }

    $male = Open-ModalFromButton $Application $Handle `
        "Browse male world model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $male.Root $relativePath $true
    Press-Button $male.Root "Use selected Skyrim mesh" $male.Handle
    Wait-ModalClosed $Application $male.Handle $male.Helper

    $female = Open-ModalFromButton $Application $Handle `
        "Browse female world model" "Select Skyrim mesh" $excluded
    Select-MeshCandidateAndWait $female.Root $relativePath $true
    Press-Button $female.Root "Use selected Skyrim mesh" $female.Handle
    Wait-ModalClosed $Application $female.Handle $female.Helper

    foreach ($field in @(
            "Male world model path relative to Meshes",
            "Female world model path relative to Meshes")) {
        if ((Get-EditValue $Root $field) -ne $expectedPath) {
            throw "The Gate-021 ARMO caller '$field' lost its accepted real Briar path."
        }
    }
}

function Set-Gate020ArmorAddonDocument {
    param(
        [System.Diagnostics.Process]$Application,
        [System.Windows.Automation.AutomationElement]$Root,
        [IntPtr]$Handle,
        [IntPtr[]]$ParentHandles,
        [string]$TypedPickerScreenshot = "",
        [string]$DataScreenshot = "",
        [switch]$ExerciseFailurePaths
    )

    if ($ExerciseFailurePaths) {
        Invoke-Button $Root "Reopen the authored Armor-addon"
        Wait-Until -Failure `
            "Unavailable Armor-addon Edit mine did not report visibly." `
            -Condition {
            Test-TextContains $Root "No source-backed Armor-addon"
        }
        Invoke-Button $Root "Start a new blank Armor-addon"
        Wait-Until -Failure "Blank Armor-addon intent retained its template." `
            -Condition {
            (Get-EditValue $Root "Male third-person model path") -eq "" -and
            (Get-EditValue $Root "Armor-addon EditorID") -like "npcm_ARMA_*"
        }
        Invoke-Button $Root `
            "Start a new Armor-addon from the reviewed template"
        Wait-Until -Failure `
            "Template Armor-addon intent did not restore source models." `
            -Condition {
            (Get-EditValue $Root "Male third-person model path") -eq
                "armor\source_addon_m.nif"
        }
    }
    Invoke-Button $Root "Override the reviewed Armor-addon"
    Wait-Until -Failure `
        "Override Armor-addon intent did not expose source EditorID." `
        -Condition {
        (Get-EditValue $Root "Armor-addon EditorID") -eq "SourceAddon"
    }

    Select-Tab $Root "Identity & Models"
    if ($ExerciseFailurePaths) {
        Set-EditValue $Root "Male third-person model path" "meshes\bad.nif"
        Wait-Until -Failure `
            "Unsafe Meshes-prefixed Armor-addon model was accepted." `
            -Condition {
            -not (Test-ButtonEnabled $Root "Save Armor-addon document")
        }
    }
    if ($Gate021) {
        Exercise-Gate021ArmorAddonMeshPickers `
            -Application $Application `
            -Root $Root `
            -Handle $Handle `
            -ParentHandles $ParentHandles `
            -ExerciseFailurePaths:$ExerciseFailurePaths
    } else {
        Set-EditValue $Root "Male third-person model path" `
            "armor\gate020_override_m.nif"
        Set-EditValue $Root "Female third-person model path" ""
        Set-EditValue $Root "Male first-person model path" `
            "armor\gate020_override_1st_m.nif"
        Set-EditValue $Root "Female first-person model path" ""
    }

    Select-Tab $Root "Slots"
    Set-ToggleState $Root "Body" $false
    Set-ToggleState $Root "Ring" $true

    Select-Tab $Root "Race & skin"
    if ($ExerciseFailurePaths) {
        Set-EditValue $Root "Primary Armor-addon race" "not-a-reference"
        Wait-Until -Failure `
            "Malformed primary Armor-addon race was accepted." -Condition {
            -not (Test-ButtonEnabled $Root "Save Armor-addon document")
        }
    }
    Set-EditValue $Root "Primary Armor-addon race" "Source.esp|0x00000900"
    Set-EditValue $Root "Male Armor-addon skin texture" `
        "Source.esp|0x00000910"
    Set-EditValue $Root "Female Armor-addon skin texture" ""
    Set-EditValue $Root "Male Armor-addon skin-swap form list" `
        "Source.esp|0x00000912"
    Set-EditValue $Root "Female Armor-addon skin-swap form list" ""
    Set-EditValue $Root "Armor-addon footstep set" "Source.esp|0x00000914"

    if ($ExerciseFailurePaths) {
        $excluded = [IntPtr[]](@($ParentHandles) + @($Handle))
        $typedPicker = Open-ModalFromButton $Application $Handle `
            "Pick or clear Armor-addon art object" "Choose art object" `
            $excluded
        Select-ListItemAt $typedPicker.Root "Typed record choices" 0
        if (-not [string]::IsNullOrWhiteSpace($TypedPickerScreenshot)) {
            Capture-Window $typedPicker.Handle $TypedPickerScreenshot
        }
        Press-Button $typedPicker.Root "Select typed record" `
            $typedPicker.Handle
        Wait-ModalClosed $Application $typedPicker.Handle $typedPicker.Helper
        Wait-Until -Failure `
            "Typed NULL selection did not clear the optional art object." `
            -Condition {
            (Get-EditValue $Root "Armor-addon art object") -eq ""
        }

        $footstepBefore = Get-EditValue $Root "Armor-addon footstep set"
        $footstepPicker = Open-ModalFromButton $Application $Handle `
            "Pick or clear Armor-addon footstep set" "Choose footstep set" `
            $excluded
        Press-Button $footstepPicker.Root "Cancel typed record selection" `
            $footstepPicker.Handle
        Wait-ModalClosed $Application $footstepPicker.Handle `
            $footstepPicker.Helper
        if ((Get-EditValue $Root "Armor-addon footstep set") -ne
            $footstepBefore) {
            throw "Typed picker Cancel changed the Armor-addon footstep set."
        }
    } else {
        Set-EditValue $Root "Armor-addon art object" ""
    }

    $initialRaces = @(Get-ListItems $Root `
        "Ordered additional Armor-addon races")
    if ($initialRaces.Count -ne 1 -or
        $initialRaces[0].Current.Name -notlike "*Source.esp|0x0000090B*") {
        throw "The reviewed ARMA did not expose its exact source additional race."
    }
    Set-EditValue $Root "Candidate additional race" "Source.esp|0x00000916"
    Invoke-Button $Root "Add qualified Armor-addon race"
    Wait-Until -Failure "A second qualified additional race was not added." `
        -Condition {
        @(Get-ListItems $Root `
            "Ordered additional Armor-addon races").Count -eq 2
    }
    Select-ListItemAt $Root "Ordered additional Armor-addon races" 1
    Invoke-Button $Root "Move selected Armor-addon race up"
    if ($ExerciseFailurePaths) {
        Invoke-Button $Root "Remove selected Armor-addon race"
        Wait-Until -Failure "Additional-race Remove did not remove one row." `
            -Condition {
            @(Get-ListItems $Root `
                "Ordered additional Armor-addon races").Count -eq 1
        }
        Set-EditValue $Root "Candidate additional race" `
            "Source.esp|0x00000916"
        Invoke-Button $Root "Add qualified Armor-addon race"
        Select-ListItemAt $Root "Ordered additional Armor-addon races" 1
        Invoke-Button $Root "Move selected Armor-addon race up"
    }
    $finalRaces = @(Get-ListItems $Root `
        "Ordered additional Armor-addon races")
    if ($finalRaces.Count -ne 2 -or
        $finalRaces[0].Current.Name -notlike "*Source.esp|0x00000916*" -or
        $finalRaces[1].Current.Name -notlike "*Source.esp|0x0000090B*") {
        throw "The ordered additional-race editor did not retain 0x916, 0x90B."
    }

    Select-Tab $Root "Data & preview"
    if ($ExerciseFailurePaths) {
        Set-EditValue $Root "Armor-addon weapon adjustment" "100000.01"
        Wait-Until -Failure `
            "Out-of-range Armor-addon weapon adjustment was accepted." `
            -Condition {
            -not (Test-ButtonEnabled $Root "Save Armor-addon document")
        }
    }
    Set-EditValue $Root "Male Armor-addon priority" "17"
    Set-EditValue $Root "Female Armor-addon priority" "29"
    Set-ToggleState $Root "Male Armor-addon weight slider enabled" $true
    Set-ToggleState $Root "Female Armor-addon weight slider enabled" $false
    Set-EditValue $Root "Armor-addon detection sound" "7"
    Set-EditValue $Root "Armor-addon weapon adjustment" "-8.25"
    Wait-Until -Failure "The complete Gate 020 ARMA did not enable Save." `
        -Condition { Test-ButtonEnabled $Root "Save Armor-addon document" }

    if ($ExerciseFailurePaths) {
        Invoke-Button $Root "Preview Armor-addon"
        Wait-Until -Failure "Armor-addon preview refusal was swallowed." `
            -Condition {
            (Test-TextContains $Root "unavailable") -and
            (Test-TextContains $Root "no render or runtime authority")
        }
        Invoke-Button $Root `
            "Delete a new Armor Addon or revert an override"
        Wait-Until -Failure `
            "Armor-addon delete/revert dependency refusal was swallowed." `
            -Condition {
            (Test-TextContains $Root "dependency evidence") -and
            (Test-TextContains $Root "unavailable")
        }
        if (-not [string]::IsNullOrWhiteSpace($DataScreenshot)) {
            Capture-Window $Handle $DataScreenshot
        }
    }
}

function Get-RelativeLabPath {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith(
            $labRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is not below the lab root: $full"
    }
    return $full.Substring($labRoot.Length + 1).Replace("\", "/")
}

$application = $null
$checks = [ordered]@{}
$activeHelpers = New-Object `
    System.Collections.Generic.List[System.Diagnostics.Process]
try {
    $application = Start-Process -FilePath $executable `
        -WorkingDirectory $publishRootFull -PassThru
    $script:mainHandle = [IntPtr]::Zero
    Wait-Until -Failure "The packaged desktop did not create a usable main window." `
        -TimeoutSeconds 120 -Condition {
        foreach ($row in [SkyGui018Native]::VisibleWindowsForProcess(
                [uint32]$application.Id)) {
            $parts = $row -split "\|", 2
            $candidate = [IntPtr][long]$parts[0]
            $rect = New-Object SkyGui018Native+Rect
            if ([SkyGui018Native]::GetWindowRect($candidate, [ref]$rect) -and
                ($rect.Right - $rect.Left) -ge 800 -and
                ($rect.Bottom - $rect.Top) -ge 600) {
                $script:mainHandle = $candidate
                return $true
            }
        }
        return $false
    }
    [void]$application.WaitForInputIdle(10000)
    [void][SkyGui018Native]::ShowWindow($mainHandle, 3)
    [void][SkyGui018Native]::SetForegroundWindow($mainHandle)
    $mainRoot = [System.Windows.Automation.AutomationElement]::FromHandle(
        $mainHandle)
    Wait-Until -Failure "The packaged shell did not finish its automation tree." `
        -TimeoutSeconds 120 -Condition {
        $null -ne (Try-FindElement $mainRoot `
            ([System.Windows.Automation.ControlType]::TabItem) `
            "Open copied Skyrim workspace task")
    }

    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $phase1Data
    Set-EditValue $mainRoot "Explicit load-order manifest" $phase1LoadOrder
    Set-EditValue $mainRoot "Fresh workspace output folder" $phase1Preflight
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The Armor task did not enable after phase-one review." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Create Skyrim armor task").Current.IsEnabled
    }
    $checks.reviewedPhaseOneClosure = $true

    Select-Tab $mainRoot "Create Skyrim armor task"
    Set-EditValue $mainRoot "Armor source plugin" "Source.esp"
    Set-EditValue $mainRoot "Armor source FormID" "0x00000A00"
    Set-EditValue $mainRoot "New armor local FormID" "0x00000B00"
    if ($isCompleteAddonGate) {
        Set-EditValue $mainRoot "New armor-addon local FormID" "0x00000C00"
        Set-EditValue $mainRoot "Armor-addon output plugin" $armorAddonPlugin
    }
    Set-EditValue $mainRoot "Armor production proposal path" $armorProposal
    Set-EditValue $mainRoot "Armor output plugin" $armorPlugin
    Invoke-Button $mainRoot "Load reviewed armor source"
    Wait-Until -Failure "The explicit source-owned Armor did not load." `
        -TimeoutSeconds 120 -Condition {
        (Test-ButtonEnabled $mainRoot "Open Skyrim armor editor") -and
        (Test-TextContains $mainRoot "3 ARMA slot-evidence row")
    }
    Capture-Window $mainHandle $screenshots.loaded
    $checks.explicitReviewedArmorLoad = $true

    $cancelModal = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
    Invoke-Button $cancelModal.Root "Start a blank new armor"
    Press-Button $cancelModal.Root "Cancel the whole armor transaction" `
        $cancelModal.Handle
    Wait-ModalClosed $application $cancelModal.Handle $cancelModal.Helper

    $escapeModal = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
    Invoke-Button $escapeModal.Root "Copy an existing armor into a new record"
    [void][SkyGui018Native]::SetForegroundWindow($escapeModal.Handle)
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Wait-ModalClosed $application $escapeModal.Handle $escapeModal.Helper

    $closeModal = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
    Invoke-Button $closeModal.Root "Override an existing armor"
    [void][SkyGui018Native]::PostMessage(
        $closeModal.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    Wait-ModalClosed $application $closeModal.Handle $closeModal.Helper
    Wait-Until -Failure "Cancel, Escape, or title close leaked Armor state." `
        -Condition {
        Test-TextContains $mainRoot "No accepted immutable ARMO document"
    }
    if ((Test-Path -LiteralPath $armorProposal) -or
        (Test-Path -LiteralPath $armorPlugin)) {
        throw "A cancelled Armor modal wrote an artifact."
    }
    $checks.cancelEscapeAndTitleCloseRollback = $true

    $armor = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
    Resize-Window $armor.Handle 1180 900
    $nameBox = Find-Element $armor.Root `
        ([System.Windows.Automation.ControlType]::Edit) "Armor display name"
    Wait-Until -Failure "Armor Name did not receive initial focus." -Condition {
        $nameBox.Current.HasKeyboardFocus
    }
    Invoke-Button $armor.Root "Continue editing an authored armor"
    Wait-Until -Failure "Unavailable Edit mine did not report visibly." -Condition {
        Test-TextContains $armor.Root "unavailable"
    }
    Invoke-Button $armor.Root "Start a blank new armor"
    Wait-Until -Failure "Blank intent retained the template name." -Condition {
        (Get-EditValue $armor.Root "Armor display name") -eq ""
    }
    Invoke-Button $armor.Root "Copy an existing armor into a new record"
    Wait-Until -Failure "Template intent did not restore source fields." -Condition {
        (Get-EditValue $armor.Root "Armor display name") -eq "Source armor"
    }
    Invoke-Button $armor.Root "Override an existing armor"
    Wait-Until -Failure "Override intent did not expose source EditorID." -Condition {
        (Get-EditValue $armor.Root "Armor EditorID") -eq "SourceArmor"
    }
    Invoke-Button $armor.Root "Copy an existing armor into a new record"
    $checks.allFourIntentsAndUnavailableEdit = $true

    Select-Tab $armor.Root "Core"
    Set-EditValue $armor.Root "Unsigned armor value" "-1"
    Wait-Until -Failure "Invalid unsigned value did not disable Save." -Condition {
        -not (Test-ButtonEnabled $armor.Root "Save immutable armor document")
    }
    Set-EditValue $armor.Root "Unsigned armor value" "10"
    Set-EditValue $armor.Root "Skyrim armor rating" "65536"
    Wait-Until -Failure "Out-of-range Skyrim rating was accepted." -Condition {
        -not (Test-ButtonEnabled $armor.Root "Save immutable armor document")
    }
    Set-EditValue $armor.Root "Skyrim armor rating" "5"
    Set-EditValue $armor.Root "Minimum X bound" "2"
    Wait-Until -Failure "Inverted bounds were accepted." -Condition {
        -not (Test-ButtonEnabled $armor.Root "Save immutable armor document")
    }
    Capture-Window $armor.Handle $screenshots.invalid
    Set-EditValue $armor.Root "Minimum X bound" "-1"
    Select-Tab $armor.Root "References & models"
    Set-EditValue $armor.Root "Qualified race reference" "not-a-reference"
    Wait-Until -Failure "Malformed race reference was accepted." -Condition {
        -not (Test-ButtonEnabled $armor.Root "Save immutable armor document")
    }
    Set-EditValue $armor.Root "Qualified race reference" "Source.esp|0x00000900"
    Wait-Until -Failure "Restored complete document did not re-enable Save." `
        -Condition { Test-ButtonEnabled $armor.Root "Save immutable armor document" }
    $checks.liveNumericBoundsAndReferenceValidation = $true

    Select-Tab $armor.Root "Armor addons & keywords"
    if (-not $isArmorAddonGate) {
    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x00000908"
    Invoke-Button $armor.Root "Add armor addon reference"
    Wait-Until -Failure "Second distinct ARMA was not appended." -Condition {
        @(Get-ListItems $armor.Root "Ordered armor addon references").Count -eq 2
    }
    Select-ListItemAt $armor.Root "Ordered armor addon references" 1
    Invoke-Button $armor.Root "Move selected armor addon up"
    Select-ListItemAt $armor.Root "Ordered armor addon references" 0
    Invoke-Button $armor.Root "Move selected armor addon down"
    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x0000090A"
    Select-ListItemAt $armor.Root "Ordered armor addon references" 1
    Invoke-Button $armor.Root "Replace selected armor addon reference"
    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x00000908"
    Select-ListItemAt $armor.Root "Ordered armor addon references" 1
    Invoke-Button $armor.Root "Replace selected armor addon reference"
    Select-ListItemAt $armor.Root "Ordered armor addon references" 1
    Invoke-Button $armor.Root "Remove selected armor addon reference"
    Wait-Until -Failure "ARMA Remove did not remove exactly one row." -Condition {
        @(Get-ListItems $armor.Root "Ordered armor addon references").Count -eq 1
    }
    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x00000908"
    Invoke-Button $armor.Root "Add armor addon reference"
    Wait-Until -Failure "ARMA Add did not restore exact row count." -Condition {
        @(Get-ListItems $armor.Root "Ordered armor addon references").Count -eq 2
    }

    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x00000906"
    Invoke-Button $armor.Root "Add armor keyword reference"
    Wait-Until -Failure "Duplicate KWDA refusal was not visible." -Condition {
        @(Get-ListItems $armor.Root "Armor keyword references").Count -eq 1 -and
        (Test-TextContains $armor.Root "already")
    }
    Set-EditValue $armor.Root `
        "Candidate qualified armor-addon or keyword reference" `
        "Source.esp|0x00000909"
    Invoke-Button $armor.Root "Add armor keyword reference"
    Wait-Until -Failure "Second unique KWDA was not appended." -Condition {
        @(Get-ListItems $armor.Root "Armor keyword references").Count -eq 2
    }
    Capture-Window $armor.Handle $screenshots.collections
    $checks.orderedArmaAndUniqueKwdaOperations = $true
    } else {
        $chooser = Open-ModalFromButton $application $armor.Handle `
            "Add armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        Resize-Window $chooser.Handle 760 760
        $searchBox = Find-Element $chooser.Root `
            ([System.Windows.Automation.ControlType]::Edit) `
            "Search compatible Armor Addons"
        Wait-Until -Failure "The ARMA search did not receive initial focus." `
            -Condition { $searchBox.Current.HasKeyboardFocus }
        Wait-Until -Failure "The reviewed ARMA catalog did not populate." `
            -Condition {
            @(Get-ListItems $chooser.Root "Compatible Armor Addons").Count -gt 0
        }
        Capture-Window $chooser.Handle $screenshots.chooser
        $chooserText = @(Get-TextValues $chooser.Root)
        if (-not (Test-TextContains $chooser.Root `
                "2 compatible ARMA candidate") -or
            -not (Test-TextContains $chooser.Root `
                "Excluded: 1 deleted, 2 stale, 1 incompatible or unverified")) {
            throw "The reviewed exclusion tally was not visible. Observed text: $($chooserText -join ' | ')"
        }
        Type-EditValue $chooser.Root "Search compatible Armor Addons" `
            "definitely-not-present" $chooser.Handle
        Wait-Until -Failure "The ARMA empty-search state was not explicit." `
            -Condition {
            @(Get-ListItems $chooser.Root "Compatible Armor Addons").Count -eq 0 -and
            (Test-TextContains $chooser.Root "No reviewed race-compatible ARMA")
        }
        Capture-Window $chooser.Handle $screenshots.chooserEmpty
        Type-EditValue $chooser.Root "Search compatible Armor Addons" `
            "SourceAddon" $chooser.Handle
        Start-Sleep -Milliseconds 750
        Capture-Window $chooser.Handle $screenshots.chooserReset
        if (-not (Test-ItemContaining $chooser.Root `
                "Compatible Armor Addons" "SourceAddon")) {
            throw "The SourceAddon search result did not repopulate. Value='$((Get-EditValue $chooser.Root 'Search compatible Armor Addons'))'; text='$((Get-TextValues $chooser.Root) -join ' | ')'"
        }
        Select-ItemContaining $chooser.Root "Compatible Armor Addons" `
            "SourceAddon"
        Invoke-Button $chooser.Root "Choose selected compatible ARMA"
        Wait-Until -Failure "Choose did not update the local ARMA row." `
            -Condition {
            Test-TextContains $chooser.Root "Source.esp|0x00000907"
        }
        Press-Button $chooser.Root "Cancel Armor-addon reference edit" `
            $chooser.Handle
        Wait-ModalClosed $application $chooser.Handle $chooser.Helper
        if (@(Get-ListItems $armor.Root `
                "Ordered armor addon references").Count -ne 1) {
            throw "Choose plus Cancel mutated the outer Armor rows."
        }

        $chooserEscape = Open-ModalFromButton $application $armor.Handle `
            "Add armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        [void][SkyGui018Native]::SetForegroundWindow($chooserEscape.Handle)
        [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
        Wait-ModalClosed $application $chooserEscape.Handle `
            $chooserEscape.Helper
        $chooserClose = Open-ModalFromButton $application $armor.Handle `
            "Add armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        [void][SkyGui018Native]::PostMessage(
            $chooserClose.Handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        Wait-ModalClosed $application $chooserClose.Handle $chooserClose.Helper
        if (@(Get-ListItems $armor.Root `
                "Ordered armor addon references").Count -ne 1) {
            throw "ARMA chooser Escape or title close mutated the outer Armor rows."
        }
        $checks.chooserSearchChooseCancelEscapeAndClose = $true

        Select-ListItemAt $armor.Root "Ordered armor addon references" 0
        $chooserDeepCancel = Open-ModalFromButton $application $armor.Handle `
            "Replace selected armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        Wait-Until -Failure "Replace did not reconstruct the current SourceAddon row." `
            -Condition {
            Test-TextContains $chooserDeepCancel.Root `
                "Source.esp|0x00000907"
        }
        $deepCancel = Open-ModalFromButton $application `
            $chooserDeepCancel.Handle "Deep edit selected Armor Addon" `
            "Skyrim Armor-addon editor" `
            @($mainHandle, $armor.Handle, $chooserDeepCancel.Handle)
        Resize-Window $deepCancel.Handle 1180 900
        if ($isCompleteAddonGate) {
            Invoke-Button $deepCancel.Root "Reopen the authored Armor-addon"
            Wait-Until -Failure `
                "Unavailable Armor-addon Edit mine did not report visibly." `
                -Condition {
                Test-TextContains $deepCancel.Root `
                    "No source-backed Armor-addon"
            }
            Invoke-Button $deepCancel.Root "Start a new blank Armor-addon"
            Invoke-Button $deepCancel.Root `
                "Start a new Armor-addon from the reviewed template"
            Invoke-Button $deepCancel.Root "Override the reviewed Armor-addon"
        }
        Set-EditValue $deepCancel.Root "Male third-person model path" `
            "armor\cancelled_deep_edit_m.nif"
        Capture-Window $deepCancel.Handle $screenshots.deepEdit
        Press-Button $deepCancel.Root "Cancel Armor-addon edit" `
            $deepCancel.Handle
        Wait-ModalClosed $application $deepCancel.Handle $deepCancel.Helper
        Wait-Until -Failure "Deep-edit Cancel lost the current ARMA row." `
            -Condition {
            (Test-TextContains $chooserDeepCancel.Root `
                "Source.esp|0x00000907") -and
            (Test-TextContains $chooserDeepCancel.Root "unchanged")
        }
        Press-Button $chooserDeepCancel.Root `
            "Cancel Armor-addon reference edit" $chooserDeepCancel.Handle
        Wait-ModalClosed $application $chooserDeepCancel.Handle `
            $chooserDeepCancel.Helper
        $checks.deepEditCancelPreservesCurrentRow = $true

        Select-ListItemAt $armor.Root "Ordered armor addon references" 0
        $chooserDeepSave = Open-ModalFromButton $application $armor.Handle `
            "Replace selected armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        Wait-Until -Failure "Replace did not reopen the SourceAddon row." `
            -Condition {
            Test-TextContains $chooserDeepSave.Root `
                "Source.esp|0x00000907"
        }
        $deepSave = Open-ModalFromButton $application `
            $chooserDeepSave.Handle "Deep edit selected Armor Addon" `
            "Skyrim Armor-addon editor" `
            @($mainHandle, $armor.Handle, $chooserDeepSave.Handle)
        Resize-Window $deepSave.Handle 1180 900
        if ($isCompleteAddonGate) {
            Set-Gate020ArmorAddonDocument `
                -Application $application `
                -Root $deepSave.Root `
                -Handle $deepSave.Handle `
                -ParentHandles @(
                    $mainHandle, $armor.Handle, $chooserDeepSave.Handle) `
                -TypedPickerScreenshot $screenshots.typedPicker `
                -DataScreenshot $screenshots.data `
                -ExerciseFailurePaths
        } else {
            Set-EditValue $deepSave.Root "Male third-person model path" `
                "armor\accepted_deep_edit_m.nif"
        }
        Press-Button $deepSave.Root "Save Armor-addon document" `
            $deepSave.Handle
        Wait-ModalClosed $application $deepSave.Handle $deepSave.Helper
        Wait-ModalClosed $application $chooserDeepSave.Handle `
            $chooserDeepSave.Helper
        Start-Sleep -Milliseconds 750
        Capture-Window $armor.Handle $screenshots.deepEditOuter
        $deepOuterRows = @(Get-ListItems $armor.Root `
            "Ordered armor addon references")
        if ($deepOuterRows.Count -ne 1) {
            throw "Committed deep edit did not reach outer Armor. Rows=$($deepOuterRows.Count); text='$((Get-TextValues $armor.Root) -join ' | ')'"
        }
        if ($isCompleteAddonGate) {
            Select-ListItemAt $armor.Root "Ordered armor addon references" 0
            $chooserAuthored = Open-ModalFromButton $application $armor.Handle `
                "Replace selected armor addon reference" "Armor addon (ARMA)" `
                @($mainHandle, $armor.Handle)
            $deepAuthored = Open-ModalFromButton $application `
                $chooserAuthored.Handle "Deep edit selected Armor Addon" `
                "Skyrim Armor-addon editor" `
                @($mainHandle, $armor.Handle, $chooserAuthored.Handle)
            Invoke-Button $deepAuthored.Root `
                "Reopen the authored Armor-addon"
            $expectedAuthoredModel = if ($Gate021) {
                "armor\Briar\Briar_1.nif"
            } else {
                "armor\gate020_override_m.nif"
            }
            Wait-Until -Failure `
                "Armor-addon Edit mine did not reconstruct the accepted draft." `
                -Condition {
                (Get-EditValue $deepAuthored.Root `
                    "Male third-person model path") -eq
                    $expectedAuthoredModel
            }
            Select-Tab $deepAuthored.Root "Race & skin"
            Wait-Until -Failure `
                "Armor-addon Edit mine lost its ordered additional races." `
                -Condition {
                @(Get-ListItems $deepAuthored.Root `
                    "Ordered additional Armor-addon races").Count -eq 2
            }
            Press-Button $deepAuthored.Root "Cancel Armor-addon edit" `
                $deepAuthored.Handle
            Wait-ModalClosed $application $deepAuthored.Handle `
                $deepAuthored.Helper
            Press-Button $chooserAuthored.Root `
                "Cancel Armor-addon reference edit" $chooserAuthored.Handle
            Wait-ModalClosed $application $chooserAuthored.Handle `
                $chooserAuthored.Helper
        }
        Press-Button $armor.Root "Cancel the whole armor transaction" `
            $armor.Handle
        Wait-ModalClosed $application $armor.Handle $armor.Helper
        if ((Test-Path -LiteralPath $armorProposal) -or
            (Test-Path -LiteralPath $armorPlugin) -or
            (Test-Path -LiteralPath $armorAddonProposal) -or
            (Test-Path -LiteralPath $armorAddonPlugin) -or
            @(Get-ChildItem -LiteralPath $outputRoot -File -Filter `
                "*.armor-addon-proposal.json").Count -ne 0) {
            throw "Outer Armor Cancel leaked a nested ARMA or parent artifact."
        }
        $checks.deepEditAutoAcceptAndOuterRollback = $true

        $armor = Open-ModalFromButton $application $mainHandle `
            "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
        Resize-Window $armor.Handle 1180 900
        Select-Tab $armor.Root "Armor addons & keywords"
        Select-ListItemAt $armor.Root "Ordered armor addon references" 0
        $chooserRelease = Open-ModalFromButton $application $armor.Handle `
            "Replace selected armor addon reference" "Armor addon (ARMA)" `
            @($mainHandle, $armor.Handle)
        if ($isCompleteAddonGate) {
            Wait-Until -Failure `
                "Release ARMA chooser did not reconstruct SourceAddon." `
                -Condition {
                Test-TextContains $chooserRelease.Root `
                    "Source.esp|0x00000907"
            }
            $releaseAddon = Open-ModalFromButton $application `
                $chooserRelease.Handle "Deep edit selected Armor Addon" `
                "Skyrim Armor-addon editor" `
                @($mainHandle, $armor.Handle, $chooserRelease.Handle)
            Resize-Window $releaseAddon.Handle 1180 900
            Set-Gate020ArmorAddonDocument `
                -Application $application `
                -Root $releaseAddon.Root `
                -Handle $releaseAddon.Handle `
                -ParentHandles @(
                    $mainHandle, $armor.Handle, $chooserRelease.Handle)
            Capture-Window $releaseAddon.Handle $screenshots.releaseAddon
            Press-Button $releaseAddon.Root "Save Armor-addon document" `
                $releaseAddon.Handle
            Wait-ModalClosed $application $releaseAddon.Handle `
                $releaseAddon.Helper
            Wait-ModalClosed $application $chooserRelease.Handle `
                $chooserRelease.Helper
        } else {
            Type-EditValue $chooserRelease.Root `
                "Search compatible Armor Addons" "ProviderSecondAddon" `
                $chooserRelease.Handle
            Wait-Until -Failure `
                "The provider-winning ARMA search did not populate." `
                -Condition {
                Test-ItemContaining $chooserRelease.Root `
                    "Compatible Armor Addons" "ProviderSecondAddon"
            }
            Select-ItemContaining $chooserRelease.Root `
                "Compatible Armor Addons" "ProviderSecondAddon"
            Invoke-Button $chooserRelease.Root "Choose selected compatible ARMA"
            Wait-Until -Failure `
                "The additional-race compatibility route was not visible." `
                -Condition {
                Test-TextContains $chooserRelease.Root `
                    "through an additional race"
            }
            Press-Button $chooserRelease.Root "Use selected Armor Addon" `
                $chooserRelease.Handle
            Wait-ModalClosed $application $chooserRelease.Handle `
                $chooserRelease.Helper
        }
        $finalArmaRows = @(Get-ListItems $armor.Root `
            "Ordered armor addon references")
        $expectedReleaseAddon = if ($isCompleteAddonGate) {
            "*Source.esp|0x00000907*"
        } else {
            "*Source.esp|0x00000908*"
        }
        if ($finalArmaRows.Count -ne 1 -or
            $finalArmaRows[0].Current.Name -notlike $expectedReleaseAddon) {
            throw "The release Armor did not retain exactly its accepted ARMA."
        }
        if ($isCompleteAddonGate) {
            Select-Tab $armor.Root "Core"
            Invoke-Button $armor.Root `
                "Recalculate BOD2 from all armor addons"
            Wait-Until -Failure `
                "Parent BOD2 ignored the accepted complete Gate 020 child." `
                -Condition {
                (Get-EditValue $armor.Root "BOD2 slot bit mask") -eq
                    "0x00000040"
            }
            Select-Tab $armor.Root "Armor addons & keywords"
        }
        Set-EditValue $armor.Root `
            "Candidate qualified armor-addon or keyword reference" `
            "Source.esp|0x00000909"
        Invoke-Button $armor.Root "Add armor keyword reference"
        Wait-Until -Failure "Second unique KWDA was not appended." -Condition {
            @(Get-ListItems $armor.Root "Armor keyword references").Count -eq 2
        }
        Capture-Window $armor.Handle $screenshots.collections
        if ($Gate021) {
            Select-Tab $armor.Root "References & models"
            Exercise-Gate021ArmorWorldMeshPickers `
                -Application $application `
                -Root $armor.Root `
                -Handle $armor.Handle `
                -ParentHandles @($mainHandle)
            $checks.allSixMeshCallerFieldsAndRollback = $true
        }
        if ($isCompleteAddonGate) {
            $checks.completeAddonEditAndParentSlotRecalculation = $true
        } else {
            $checks.additionalRaceArmaUseAndExactOuterReplacement = $true
        }
    }

    Select-Tab $armor.Root "Preview boundary"
    Invoke-Button $armor.Root "Request advisory armor preview"
    Wait-Until -Failure "Typed preview failure was swallowed." -Condition {
        (Test-TextContains $armor.Root "unavailable") -and
        (Test-TextContains $armor.Root "no render or runtime authority")
    }
    Capture-Window $armor.Handle $screenshots.preview
    $checks.visiblePreviewFailureWithoutAuthority = $true

    Select-Tab $armor.Root "Core"
    Set-EditValue $armor.Root "Armor display name" "Generated travel armor"
    $nameBox = Find-Element $armor.Root `
        ([System.Windows.Automation.ControlType]::Edit) "Armor display name"
    [void][SkyGui018Native]::SetForegroundWindow($armor.Handle)
    $nameBox.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Wait-ModalClosed $application $armor.Handle $armor.Helper
    $expectedArmaSummary = if ($isArmorAddonGate) {
        "1 ordered ARMA"
    } else {
        "2 ordered ARMA"
    }
    Wait-Until -Failure "Accepted Armor document did not reach its owner." -Condition {
        (Test-TextContains $mainRoot "npcm_ARMO_SourceArmor") -and
        (Test-TextContains $mainRoot $expectedArmaSummary) -and
        (Test-TextContains $mainRoot "2 unique KWDA")
    }
    $checks.enterSavedImmutableDocument = $true

    $reopen = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim armor editor" "Skyrim Armor editor" @($mainHandle)
    if ((Get-EditValue $reopen.Root "Armor display name") -ne
        "Generated travel armor") {
        throw "Edit-authored reopening lost the accepted Armor document."
    }
    Invoke-Button $reopen.Root "Continue editing an authored armor"
    Wait-Until -Failure "Edit mine could not reload the accepted document." -Condition {
        (Get-EditValue $reopen.Root "Armor display name") -eq
            "Generated travel armor"
    }
    Press-Button $reopen.Root "Cancel the whole armor transaction" $reopen.Handle
    Wait-ModalClosed $application $reopen.Handle $reopen.Helper
    Wait-Until -Failure "Later Cancel erased prior accepted Armor state." -Condition {
        Test-TextContains $mainRoot "npcm_ARMO_SourceArmor"
    }
    $checks.editAuthoredReopenAndCancelRetention = $true

    Select-Tab $mainRoot "Browse and author Skyrim outfits task"
    Set-EditValue $mainRoot "Outfit template plugin" "OutfitProvider.esp"
    Set-EditValue $mainRoot "Outfit template FormID" "0x00000900"
    Set-EditValue $mainRoot "New outfit local FormID" "0x00000A30"
    Set-EditValue $mainRoot "Outfit initial leveled-list seed" "17"
    Set-EditValue $mainRoot "Outfit proposal path" $cancelledOutfitProposal
    Set-EditValue $mainRoot "Outfit output plugin" $cancelledOutfitPlugin
    Invoke-Button $mainRoot "Load reviewed outfit catalog"
    Wait-Until -Failure "Phase-one outfit catalog did not load." `
        -TimeoutSeconds 120 -Condition {
        Test-ButtonEnabled $mainRoot "Open Skyrim outfit workbench"
    }
    $outer = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim outfit workbench" "Choose or author a Skyrim outfit" `
        @($mainHandle)
    Press-Button $outer.Root "Begin a new outfit" $outer.Handle
    Set-EditValue $outer.Root "Filter armor and leveled-list items" "Source armor"
    Select-ItemContaining $outer.Root "Available ARMO and LVLI items" `
        "Source armor"
    $child = Open-ModalFromButton $application $outer.Handle `
        "Open the typed new armor child editor" "Skyrim Armor editor" `
        @($mainHandle, $outer.Handle)
    Set-EditValue $child.Root "Armor display name" "Cancelled child armor"
    Press-Button $child.Root "Save immutable armor document" $child.Handle
    Wait-ModalClosed $application $child.Handle $child.Helper
    Wait-Until -Failure "Accepted child did not remain inside outer draft." -Condition {
        (Test-TextContains $outer.Root "Cancelled child armor") -or
        @(Get-ListItems $outer.Root "Authored outfit equip order").Count -eq 1
    }
    Capture-Window $outer.Handle $screenshots.cancelledChild
    Press-Button $outer.Root "Cancel the whole outfit transaction" $outer.Handle
    Wait-ModalClosed $application $outer.Handle $outer.Helper
    if ((Test-Path -LiteralPath $cancelledOutfitProposal) -or
        (Test-Path -LiteralPath $cancelledOutfitPlugin) -or
        (Test-Path -LiteralPath $armorProposal) -or
        (Test-Path -LiteralPath $armorPlugin) -or
        (Test-Path -LiteralPath $armorAddonProposal) -or
        (Test-Path -LiteralPath $armorAddonPlugin)) {
        throw "Outer Cancel leaked an ARMO or OTFT artifact."
    }
    $checks.outerCancelDropsAuthoredChildAndArtifacts = $true

    Select-Tab $mainRoot "Create Skyrim armor task"
    Invoke-Button $mainRoot "Review armor proposal"
    Wait-Until -Failure "Armor Review did not remain proposal-only." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $armorProposal) -and
        (-not $isCompleteAddonGate -or
            (Test-Path -LiteralPath $armorAddonProposal)) -and
        -not (Test-Path -LiteralPath $armorPlugin) -and
        (-not $isCompleteAddonGate -or
            -not (Test-Path -LiteralPath $armorAddonPlugin)) -and
        (Test-TextContains $mainRoot "Ready to write")
    }
    if ($isCompleteAddonGate) {
        $checks.parentAndChildReviewAreProposalOnly = $true
    } else {
        $checks.armorReviewProposalOnly = $true
    }
    Invoke-Button $mainRoot "Write and verify armor output"
    Wait-Until -Failure "Armor write did not reach a terminal verdict." `
        -TimeoutSeconds 180 -Condition {
        (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED") -or
        (Test-TextContains $mainRoot "Write refused") -or
        (Test-TextContains $mainRoot "Verification failed") -or
        (Test-TextContains $mainRoot "Write failed")
    }
    if (-not (Test-Path -LiteralPath $armorPlugin) -or
        ($isCompleteAddonGate -and -not (Test-Path -LiteralPath $armorAddonPlugin)) -or
        -not (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")) {
        $diagnosticRows = @()
        try {
            $diagnosticRows = @(Get-ListItems $mainRoot `
                "Armor transaction diagnostics" | ForEach-Object {
                    $_.Current.Name
                })
        } catch {
            $diagnosticRows = @("Diagnostics automation unavailable: $($_.Exception.Message)")
        }
        throw "Armor output did not survive two readbacks. Diagnostics: $($diagnosticRows -join ' | '). Visible text: $((Get-TextValues $mainRoot) -join ' | ')"
    }
    Capture-Window $mainHandle $screenshots.armorVerified
    if ($isCompleteAddonGate) {
        $checks.atomicChildThenParentWriteAndReadbacks = $true
    } else {
        $checks.armorWriteAndTwoReadbacks = $true
    }

    Copy-Item -LiteralPath $armorPlugin `
        -Destination (Join-Path $phase2Data "GeneratedArmor.esp")
    if ($isCompleteAddonGate) {
        Copy-Item -LiteralPath $armorAddonPlugin `
            -Destination (Join-Path $phase2Data "GeneratedAddonOverride.esp")
    }
    Select-Tab $mainRoot "Open copied Skyrim workspace task"
    Set-EditValue $mainRoot "Copied Skyrim Data folder" $phase2Data
    Set-EditValue $mainRoot "Explicit load-order manifest" $phase2LoadOrder
    Set-EditValue $mainRoot "Fresh workspace output folder" $phase2Preflight
    Invoke-Button $mainRoot "Review copied Skyrim workspace"
    Wait-Until -Failure "The phase-two outfit task did not enable." `
        -TimeoutSeconds 120 -Condition {
        (Find-Element $mainRoot ([System.Windows.Automation.ControlType]::TabItem) `
            "Browse and author Skyrim outfits task").Current.IsEnabled
    }
    $checks.freshPhaseTwoClosureContainsVerifiedArmor = $true

    Select-Tab $mainRoot "Browse and author Skyrim outfits task"
    Set-EditValue $mainRoot "Outfit template plugin" "OutfitProviderAfter.esp"
    Set-EditValue $mainRoot "Outfit template FormID" "0x00000900"
    Set-EditValue $mainRoot "New outfit local FormID" "0x00000A20"
    Set-EditValue $mainRoot "Outfit initial leveled-list seed" "17"
    Set-EditValue $mainRoot "Outfit proposal path" $outfitProposal
    Set-EditValue $mainRoot "Outfit output plugin" $outfitPlugin
    Invoke-Button $mainRoot "Load reviewed outfit catalog"
    Wait-Until -Failure "Phase-two outfit catalog did not load generated ARMO." `
        -TimeoutSeconds 120 -Condition {
        (Test-ButtonEnabled $mainRoot "Open Skyrim outfit workbench") -and
        (Test-TextContains $mainRoot "ARMO")
    }
    $finalOutfit = Open-ModalFromButton $application $mainHandle `
        "Open Skyrim outfit workbench" "Choose or author a Skyrim outfit" `
        @($mainHandle)
    Select-Tab $finalOutfit.Root "Author"
    Set-EditValue $finalOutfit.Root "New outfit EditorID" `
        "npcm_OTFT_GeneratedArmor"
    Press-Button $finalOutfit.Root "Begin new" $finalOutfit.Handle
    Set-EditValue $finalOutfit.Root "Filter armor and leveled-list items" `
        "Generated travel armor"
    Select-ItemContaining $finalOutfit.Root "Available ARMO and LVLI items" `
        "Generated travel armor"
    Invoke-Button $finalOutfit.Root "Add selected item to outfit"
    Wait-Until -Failure "Generated ARMO did not become the sole outfit item." `
        -Condition {
        @(Get-ListItems $finalOutfit.Root "Authored outfit equip order").Count -eq 1
    }
    Capture-Window $finalOutfit.Handle $screenshots.outfit
    Press-Button $finalOutfit.Root `
        "Use the selected outfit or save the authored proposal" $finalOutfit.Handle
    Wait-ModalClosed $application $finalOutfit.Handle $finalOutfit.Helper
    Wait-Until -Failure "Generated ARMO did not enter the real outfit proposal." `
        -Condition { Test-TextContains $mainRoot "New OTFT staged with 1 ordered item" }
    Invoke-Button $mainRoot "Review outfit proposal"
    Wait-Until -Failure "Outfit Review did not remain proposal-only." `
        -TimeoutSeconds 120 -Condition {
        (Test-Path -LiteralPath $outfitProposal) -and
        -not (Test-Path -LiteralPath $outfitPlugin) -and
        (Test-TextContains $mainRoot "Ready to write")
    }
    Invoke-Button $mainRoot "Write and verify outfit output"
    Wait-Until -Failure "Generated-ARMO outfit failed two readbacks." `
        -TimeoutSeconds 180 -Condition {
        (Test-Path -LiteralPath $outfitPlugin) -and
        (Test-TextContains $mainRoot "STATIC_PASS_RUNTIME_REQUIRED")
    }
    Capture-Window $mainHandle $screenshots.final
    $checks.generatedArmorUsedInRealOutfit = $true

    if ($Gate021) {
        $rawText = (& python $rawVerifier `
            --armor-addon-plugin $armorAddonPlugin `
            --armor-addon-proposal $armorAddonProposal `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --armor-addon-nif $realBriarArmaNif `
            --armor-world-nif $realBriarWorldNif `
            --report $rawReport 2>&1) -join "`n"
    } elseif ($Gate020) {
        $rawText = (& python $rawVerifier `
            --armor-addon-plugin $armorAddonPlugin `
            --armor-addon-proposal $armorAddonProposal `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --report $rawReport 2>&1) -join "`n"
    } else {
        $rawText = (& python $rawVerifier `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --report $rawReport 2>&1) -join "`n"
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Independent raw ARMO/OTFT verification failed: $rawText"
    }
    $rawAudit = Get-Content -LiteralPath $rawReport -Raw | ConvertFrom-Json
    if (-not $rawAudit.passed) {
        throw "Independent raw audit did not prove exact ARMO and follow-on OTFT."
    }
    $checks.independentRawArmorAndOutfitAudit = $true

    if ($Gate021) {
        $negativeText = (& python $rawVerifier `
            --armor-addon-plugin $armorAddonPlugin `
            --armor-addon-proposal $armorAddonProposal `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --armor-addon-nif $realBriarArmaNif `
            --armor-world-nif $realBriarWorldNif `
            --expect-source-values `
            --report $negativeRawReport 2>&1) -join "`n"
    } elseif ($Gate020) {
        $negativeText = (& python $rawVerifier `
            --armor-addon-plugin $armorAddonPlugin `
            --armor-addon-proposal $armorAddonProposal `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --expect-source-values `
            --expected-parent-armor-addon "Source.esp|0x00000908" `
            --report $negativeRawReport 2>&1) -join "`n"
    } elseif ($Gate019) {
        $negativeText = (& python $rawVerifier `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --expected-armor-addon "Source.esp|0x00000907" `
            --expected-armor-addon "Source.esp|0x00000908" `
            --report $negativeRawReport 2>&1) -join "`n"
    } else {
        $negativeText = (& python $rawVerifier `
            --armor-plugin $armorPlugin `
            --armor-proposal $armorProposal `
            --outfit-plugin $outfitPlugin `
            --outfit-proposal $outfitProposal `
            --expected-editor-id "DeliberatelyWrongArmorEditorId" `
            --report $negativeRawReport 2>&1) -join "`n"
    }
    if ($LASTEXITCODE -eq 0) {
        throw "The raw verifier accepted the deliberately wrong expectation: $negativeText"
    }
    $negativeAudit = Get-Content -LiteralPath $negativeRawReport -Raw |
        ConvertFrom-Json
    if ($negativeAudit.passed -or $negativeAudit.failures.Count -lt 2) {
        throw "The negative control did not expose binary and proposal mismatch."
    }
    $checks.rawVerifierRejectsWrongExpectation = $true

    Press-Button $mainRoot "Close NPC Studio" $mainHandle
    if (-not $application.WaitForExit(30000)) {
        throw "The packaged desktop did not close after acceptance."
    }
    $checks.cleanProcessExit = $true

    $publishFiles = @(Get-ChildItem -LiteralPath $publishRootFull -Recurse -File)
    $report = [ordered]@{
        schema = "npcmanager.sky-gui-$gateId.packaged-acceptance.v1"
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
            root = Get-RelativeLabPath $fixtureRoot
            expected = $expected
            phase1SourceSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $phase1Data "Source.esp") -Algorithm SHA256).Hash
            phase2ProviderSha256 = (Get-FileHash -LiteralPath `
                (Join-Path $phase2Data "OutfitProviderAfter.esp") `
                -Algorithm SHA256).Hash
        }
        outputs = [ordered]@{
            armorProposal = Get-RelativeLabPath $armorProposal
            armorProposalSha256 = (Get-FileHash -LiteralPath $armorProposal `
                -Algorithm SHA256).Hash
            armorPlugin = Get-RelativeLabPath $armorPlugin
            armorPluginSha256 = (Get-FileHash -LiteralPath $armorPlugin `
                -Algorithm SHA256).Hash
            outfitProposal = Get-RelativeLabPath $outfitProposal
            outfitProposalSha256 = (Get-FileHash -LiteralPath $outfitProposal `
                -Algorithm SHA256).Hash
            outfitPlugin = Get-RelativeLabPath $outfitPlugin
            outfitPluginSha256 = (Get-FileHash -LiteralPath $outfitPlugin `
                -Algorithm SHA256).Hash
            rawAudit = Get-RelativeLabPath $rawReport
            negativeControlAudit = Get-RelativeLabPath $negativeRawReport
        }
        screenshots = [ordered]@{}
        currentNpcRuntimeScreenshot = [ordered]@{
            path = Get-RelativeLabPath $currentNpcScreenshot
            sha256 = (Get-FileHash -LiteralPath $currentNpcScreenshot `
                -Algorithm SHA256).Hash
            scope = "User-supplied gross-render evidence only; not provider or Gate-$gateId runtime authority."
        }
        pluginAuthority = $true
        offEngineMeshPreviewAuthority = [bool]$Gate021
        previewAuthority = $false
        equipmentAuthority = $false
        runtimeAuthority = $false
        visualAuthority = $false
        verdict = "STATIC_PASS_RUNTIME_REQUIRED"
    }
    if ($isCompleteAddonGate) {
        $report.outputs.armorAddonProposal =
            Get-RelativeLabPath $armorAddonProposal
        $report.outputs.armorAddonProposalSha256 =
            (Get-FileHash -LiteralPath $armorAddonProposal `
                -Algorithm SHA256).Hash
        $report.outputs.armorAddonPlugin =
            Get-RelativeLabPath $armorAddonPlugin
        $report.outputs.armorAddonPluginSha256 =
            (Get-FileHash -LiteralPath $armorAddonPlugin `
                -Algorithm SHA256).Hash
        $report.fixture.phase2ArmorAddonOutputSha256 =
            (Get-FileHash -LiteralPath `
                (Join-Path $phase2Data "GeneratedAddonOverride.esp") `
                -Algorithm SHA256).Hash
    }
    foreach ($entry in $screenshots.GetEnumerator()) {
        $report.screenshots[$entry.Key] = [ordered]@{
            path = Get-RelativeLabPath $entry.Value
            sha256 = (Get-FileHash -LiteralPath $entry.Value `
                -Algorithm SHA256).Hash
        }
    }
    $json = $report | ConvertTo-Json -Depth 12
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
