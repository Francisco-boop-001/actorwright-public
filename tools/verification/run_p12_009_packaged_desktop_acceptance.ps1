param(
    [string]$PublishRoot = "",
    [string]$EvidenceRoot = "",
    [string]$RunTag = "20260725-1",
    [string]$ReportPath = "",
    [string]$ReferenceImagePath = "",
    [switch]$ExpectMultipleFaceRefusal
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class P12009DesktopNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(
        EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(
        IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern int GetWindowText(
        IntPtr handle, StringBuilder text, int maximum);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool MoveWindow(
        IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool GetWindowRect(
        IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool PrintWindow(
        IntPtr hWnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError=true)]
    public static extern void mouse_event(
        uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static string[] VisibleWindowsForProcess(uint processId)
    {
        var rows = new List<string>();
        EnumWindows((handle, parameter) =>
        {
            uint candidate;
            GetWindowThreadProcessId(handle, out candidate);
            if (candidate != processId || !IsWindowVisible(handle))
                return true;
            var title = new StringBuilder(512);
            GetWindowText(handle, title, title.Capacity);
            rows.Add(handle.ToInt64().ToString() + "|" + title);
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
"@

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..")).Path
$labRoot = (Resolve-Path -LiteralPath (Join-Path $projectRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    $PublishRoot = Join-Path $projectRoot "03-builds\work\p12-009-desktop-publish-1"
}
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $projectRoot "03-builds\work\p12-009-desktop-evidence-$RunTag"
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $projectRoot "05-reports\p12-009-packaged-desktop-acceptance-$RunTag.json"
}

$publishRootFull = (Resolve-Path -LiteralPath $PublishRoot).Path
$evidenceRootFull = [IO.Path]::GetFullPath($EvidenceRoot)
$reportPathFull = [IO.Path]::GetFullPath($ReportPath)
$executable = Join-Path $publishRootFull "NpcManager.Desktop.exe"
$dataRoot = Join-Path $projectRoot "01-source-copies\sophia-live-closure-20260723\Data"
$loadOrder = Join-Path $projectRoot "tools\fixtures\p12-009\ui-load-order.json"
$baseline = Join-Path $labRoot "Resources\Sophia Loren-112955-1-00-1709402972\Data\SKSE\Plugins\CharGen\Presets\Sophia Loren COR.jslot"
if ([string]::IsNullOrWhiteSpace($ReferenceImagePath)) {
    $preferredReference =
        Join-Path $labRoot "Resources\Screenshot\reference 1.webp"
    $fallbackReference =
        Join-Path $labRoot "Resources\Screenshot\Chel reference image.png"
    $ReferenceImagePath = if (Test-Path -LiteralPath $preferredReference) {
        $preferredReference
    } else {
        $fallbackReference
    }
}
$referenceImage = [IO.Path]::GetFullPath($ReferenceImagePath)
$sourceRequest = Join-Path $projectRoot "01-source-copies\sophia-live-closure-20260723\execution-request.json"
$sessionParent = Join-Path $projectRoot "03-builds\work\reference-preset-desktop"
$startupOutput = Join-Path $projectRoot "03-builds\work\p12-009-ui-startup-output-$RunTag"

foreach ($path in @(
        $executable,
        $dataRoot,
        $loadOrder,
        $baseline,
        $referenceImage,
        $sourceRequest,
        (Join-Path $publishRootFull "runtime\reference-preset\runtime-asset-manifest.json"))) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required P12-009 packaged-desktop input is absent: $path"
    }
}
foreach ($path in @($evidenceRootFull, $reportPathFull, $startupOutput)) {
    if (Test-Path -LiteralPath $path) {
        throw "Acceptance path exists and will not be overwritten: $path"
    }
}
if (-not $evidenceRootFull.StartsWith(
        $projectRoot + "\", [StringComparison]::OrdinalIgnoreCase) -or
    -not $reportPathFull.StartsWith(
        $projectRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Acceptance evidence must stay below the NPC Manager project root."
}
if ($dataRoot.StartsWith(
        "F:\ExampleGame", [StringComparison]::OrdinalIgnoreCase)) {
    throw "The packaged desktop acceptance may not read the protected live root."
}

New-Item -ItemType Directory -Path $evidenceRootFull | Out-Null
$screenshotRoot = Join-Path $evidenceRootFull "screenshots"
$inputRoot = Join-Path $evidenceRootFull "input"
New-Item -ItemType Directory -Path $screenshotRoot | Out-Null
New-Item -ItemType Directory -Path $inputRoot | Out-Null

$startupRequest = Get-Content -LiteralPath $sourceRequest -Raw | ConvertFrom-Json
$startupRequest.output.root = "projects/NpcManagerReimplementation/03-builds/work/p12-009-ui-startup-output-$RunTag"
$startupRequest.output.plugin = "P12009UiStartup.esp"
$startupRequestPath = Join-Path $inputRoot "startup-request.json"
[IO.File]::WriteAllText(
    $startupRequestPath,
    ($startupRequest | ConvertTo-Json -Depth 30) + "`n",
    [Text.UTF8Encoding]::new($false))
$startupRequestHash = (Get-FileHash -LiteralPath $startupRequestPath -Algorithm SHA256).Hash

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$Failure
    )
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            if (& $Condition) {
                return
            }
        }
        catch {
            if ($stopwatch.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                throw
            }
        }
        Start-Sleep -Milliseconds 200
    } while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw $Failure
}

function Find-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Wait-Element {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [int]$TimeoutSeconds = 30,
        [switch]$Enabled
    )
    $script:waitedElement = $null
    Wait-Until -TimeoutSeconds $TimeoutSeconds `
        -Failure "UI Automation element '$Name' was not ready." `
        -Condition {
            $candidate = Find-Element $Root $Name
            if ($null -eq $candidate) {
                return $false
            }
            if ($Enabled -and -not $candidate.Current.IsEnabled) {
                return $false
            }
            $script:waitedElement = $candidate
            return $true
        }
    return $script:waitedElement
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Select-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
}

function Set-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )
    Wait-Until -TimeoutSeconds 60 `
        -Failure "UI Automation value '$($Element.Current.Name)' remained disabled." `
        -Condition { $Element.Current.IsEnabled }
    $pattern = $Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Value)
    Wait-Until -TimeoutSeconds 30 `
        -Failure "UI Automation value '$($Element.Current.Name)' did not retain the exact requested value '$Value'." `
        -Condition {
            return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value -eq $Value
        }
}

function Get-ElementValue {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern,
            [ref]$pattern)) {
        return ([System.Windows.Automation.ValuePattern]$pattern).Current.Value
    }
    return $Element.Current.ItemStatus
}

function Get-ItemStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )
    $element = Find-Element $Root $Name
    if ($null -eq $element) {
        return ""
    }
    return $element.Current.ItemStatus
}

function Wait-ItemStatus {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name,
        [scriptblock]$Predicate,
        [int]$TimeoutSeconds,
        [string]$Failure
    )
    $script:lastItemStatus = ""
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Failure $Failure -Condition {
        $script:lastItemStatus = Get-ItemStatus $Root $Name
        return & $Predicate $script:lastItemStatus
    }
    return $script:lastItemStatus
}

function Get-DescendantItems {
    param([System.Windows.Automation.AutomationElement]$Root)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    return @($Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition))
}

function Capture-Window {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [string]$Name,
        [string]$State
    )
    $handle = [IntPtr][long]$Window.Current.NativeWindowHandle
    if ($handle -eq [IntPtr]::Zero) {
        throw "The product window has no native handle for direct rendering."
    }
    $nativeRect = [P12009DesktopNative+RECT]::new()
    if (-not [P12009DesktopNative]::GetWindowRect(
            $handle, [ref]$nativeRect)) {
        throw "The product window rectangle could not be read."
    }
    $width = $nativeRect.Right - $nativeRect.Left
    $height = $nativeRect.Bottom - $nativeRect.Top
    if ($width -lt 200 -or $height -lt 200) {
        throw "The product window has no usable screenshot bounds."
    }
    $path = Join-Path $screenshotRoot "$Name.png"
    $captured = $false
    $lastCaptureError = $null
    foreach ($attempt in 1..5) {
        $bitmap = [Drawing.Bitmap]::new(
            $width,
            $height,
            [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $deviceContext = $graphics.GetHdc()
                try {
                    if (-not [P12009DesktopNative]::PrintWindow(
                            $handle, $deviceContext, 2)) {
                        throw "The product window could not be rendered directly."
                    }
                }
                finally {
                    $graphics.ReleaseHdc($deviceContext)
                }
            }
            finally {
                $graphics.Dispose()
            }
            $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
            $captured = $true
            break
        }
        catch {
            $lastCaptureError = $_
            Start-Sleep -Milliseconds 500
        }
        finally {
            $bitmap.Dispose()
        }
    }
    if (-not $captured) {
        throw $lastCaptureError
    }
    $script:screenshots.Add([ordered]@{
        state = $State
        path = $path.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        executableSha256 = $script:executableSha256
        inputSetSha256 = $script:inputSetSha256
        width = $width
        height = $height
    })
    Write-Host "SCREENSHOT $State $path"
}

function Bring-ElementIntoView {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollItemPattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.ScrollItemPattern]$pattern).ScrollIntoView()
    }
    $Element.SetFocus()
    Start-Sleep -Milliseconds 250
}

function Click-PhysicalElement {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Windows.Automation.AutomationElement]$Window
    )
    [P12009DesktopNative]::SetForegroundWindow(
        [IntPtr][long]$Window.Current.NativeWindowHandle) | Out-Null
    $Window.SetFocus()
    Start-Sleep -Milliseconds 150
    $elementRect = $Element.Current.BoundingRectangle
    $windowRect = $Window.Current.BoundingRectangle
    $left = [Math]::Max($elementRect.Left + 4, $windowRect.Left + 8)
    $right = [Math]::Min($elementRect.Right - 4, $windowRect.Right - 8)
    $top = [Math]::Max($elementRect.Top + 4, $windowRect.Top + 40)
    $bottom = [Math]::Min($elementRect.Bottom - 4, $windowRect.Bottom - 8)
    if ($right -le $left -or $bottom -le $top) {
        throw "The UI Automation element '$($Element.Current.Name)' has no visible clickable bounds."
    }
    $x = [int][Math]::Round(($left + $right) / 2)
    $y = [int][Math]::Round(($top + $bottom) / 2)
    try {
        $clickable = $Element.GetClickablePoint()
        $x = [int][Math]::Round($clickable.X)
        $y = [int][Math]::Round($clickable.Y)
    }
    catch {
        # The bounded visible-center fallback above is deterministic for WPF
        # Image elements that do not expose a UIA clickable point.
    }
    [P12009DesktopNative]::SetCursorPos($x, $y) | Out-Null
    [P12009DesktopNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [P12009DesktopNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
}

function Click-PhysicalPoint {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [int]$X,
        [int]$Y
    )
    [P12009DesktopNative]::SetForegroundWindow(
        [IntPtr][long]$Window.Current.NativeWindowHandle) | Out-Null
    $Window.SetFocus()
    Start-Sleep -Milliseconds 150
    [P12009DesktopNative]::SetCursorPos($X, $Y) | Out-Null
    [P12009DesktopNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [P12009DesktopNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
}

function Add-ReferenceImage {
    param(
        [System.Windows.Automation.AutomationElement]$Window,
        [Diagnostics.Process]$Process,
        [string]$Path
    )
    $addButton = Wait-Element $Window "Add reference image" 20 -Enabled
    Bring-ElementIntoView $addButton
    $helperOut = Join-Path $evidenceRootFull "add-image-helper.stdout.log"
    $helperError = Join-Path $evidenceRootFull "add-image-helper.stderr.log"
    $helperCode = @"
`$ErrorActionPreference = "Stop"
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
`$window =
    [System.Windows.Automation.AutomationElement]::FromHandle(
        [IntPtr][long]$($Window.Current.NativeWindowHandle))
if (`$null -eq `$window) {
    throw "The product window is absent."
}
`$nameCondition =
    [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        "Add reference image")
`$button = `$window.FindFirst(
    [System.Windows.Automation.TreeScope]::Descendants,
    `$nameCondition)
if (`$null -eq `$button) {
    throw "The Add reference image action is absent."
}
`$pattern = `$button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
([System.Windows.Automation.InvokePattern]`$pattern).Invoke()
"@
    $encoded = [Convert]::ToBase64String(
        [Text.Encoding]::Unicode.GetBytes($helperCode))
    $helper = Start-Process -FilePath "powershell.exe" `
        -ArgumentList @("-NoProfile", "-STA", "-EncodedCommand", $encoded) `
        -WindowStyle Hidden `
        -RedirectStandardOutput $helperOut `
        -RedirectStandardError $helperError `
        -PassThru
    $script:fileDialog = $null
    try {
        Wait-Until -TimeoutSeconds 30 `
            -Failure "The packaged product did not open its reference-image chooser." `
            -Condition {
                foreach ($row in
                    [P12009DesktopNative]::VisibleWindowsForProcess(
                        [uint32]$Process.Id)) {
                    $parts = $row -split "\|", 2
                    if ($parts[1].IndexOf(
                            "Choose one reference image",
                            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                        continue
                    }
                    $candidate =
                        [System.Windows.Automation.AutomationElement]::FromHandle(
                            [IntPtr][long]$parts[0])
                    if ($null -eq $candidate) {
                        continue
                    }
                    $script:fileDialog = $candidate
                    return $true
                }
                return $false
            }
        $fileNameCondition =
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                "1148")
        $fileName = $script:fileDialog.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants,
            $fileNameCondition)
        if ($null -eq $fileName) {
            throw "The reference-image chooser has no file-name field."
        }
        Set-ElementValue $fileName $Path
        Wait-Until -TimeoutSeconds 10 `
            -Failure "The reference-image chooser did not retain the exact requested path." `
            -Condition {
                return (Get-ElementValue $fileName) -eq $Path
            }
        $fileName.SetFocus()
        Start-Sleep -Milliseconds 500
        [Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Wait-Until -TimeoutSeconds 30 `
            -Failure "The reference-image chooser did not close." `
            -Condition {
                try {
                    return $script:fileDialog.Current.IsOffscreen
                }
                catch {
                    return $true
                }
            }
        if (-not $helper.WaitForExit(30000)) {
            throw "The reference-image helper did not return after the chooser closed."
        }
        $helper.Refresh()
        $helperExitCode = $helper.ExitCode
        if ($helperExitCode -ne 0) {
            $detail = if (Test-Path -LiteralPath $helperError) {
                Get-Content -LiteralPath $helperError -Raw
            } else {
                "No helper diagnostic was captured."
            }
            $script:notes.Add(
                "add-image-helper-exit=$helperExitCode`: $detail")
        }
        $imageList = Wait-Element $Window "Reference image list" 30
        Wait-Until -TimeoutSeconds 30 `
            -Failure "The chosen reference image was not retained by the packaged product." `
            -Condition {
                return (Get-DescendantItems $imageList).Count -eq 1
            }
        $imageItem = (Get-DescendantItems $imageList)[0]
        if ($imageItem.Current.ItemStatus.IndexOf(
                $Path, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "The packaged product retained a different reference image than the exact chosen path. Name='$($imageItem.Current.Name)' ItemStatus='$($imageItem.Current.ItemStatus)' Expected='$Path'."
        }
    }
    finally {
        if (-not $helper.HasExited) {
            Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Set-Check {
    param(
        [System.Collections.IDictionary]$Checks,
        [string]$Name,
        [bool]$Value
    )
    $Checks[$Name] = $Value
    Write-Host ("CHECK {0} {1}" -f $Name, $(if ($Value) { "PASS" } else { "FAIL" }))
}

$inputAuthorities = @(
    [ordered]@{
        role = "startup-request-source"
        path = $sourceRequest.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath $sourceRequest -Algorithm SHA256).Hash
    },
    [ordered]@{
        role = "startup-request-exercised"
        path = $startupRequestPath.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = $startupRequestHash
    },
    [ordered]@{
        role = "reviewed-load-order"
        path = $loadOrder.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath $loadOrder -Algorithm SHA256).Hash
    },
    [ordered]@{
        role = "baseline-jslot"
        path = $baseline.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash
    },
    [ordered]@{
        role = "reference-image"
        path = $referenceImage.Substring($labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath $referenceImage -Algorithm SHA256).Hash
    },
    [ordered]@{
        role = "packaged-runtime-manifest"
        path = (Join-Path $publishRootFull "runtime\reference-preset\runtime-asset-manifest.json").Substring(
            $labRoot.Length + 1).Replace("\", "/")
        sha256 = (Get-FileHash -LiteralPath (
            Join-Path $publishRootFull "runtime\reference-preset\runtime-asset-manifest.json") -Algorithm SHA256).Hash
    }
)
$inputSetMaterial = ($inputAuthorities | ConvertTo-Json -Depth 6 -Compress)
$inputSetHasher = [Security.Cryptography.SHA256]::Create()
try {
    $inputSetSha256 = [BitConverter]::ToString(
        $inputSetHasher.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes($inputSetMaterial))).Replace("-", "")
}
finally {
    $inputSetHasher.Dispose()
}
$executableSha256 =
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
$screenshots = [Collections.Generic.List[object]]::new()
$checks = [ordered]@{}
$notes = [Collections.Generic.List[string]]::new()
$application = $null
$mainWindow = $null
$verifiedPresetPath = ""
$verifiedPresetHash = ""
$npcOutputRoot = ""
$packageManifest = ""
$failure = $null
$productStdout = Join-Path $evidenceRootFull "product.stdout.log"
$productStderr = Join-Path $evidenceRootFull "product.stderr.log"
$productExitCode = $null
$productExitedDuringComparison = $false
$preExistingSessionRoots = @()
if (Test-Path -LiteralPath $sessionParent) {
    $preExistingSessionRoots = @(
        Get-ChildItem -LiteralPath $sessionParent -Directory |
            Select-Object -ExpandProperty FullName)
}

try {
    $arguments = @(
        "--race-menu-request",
        "`"$startupRequestPath`"",
        "--race-menu-request-sha256",
        $startupRequestHash)
    $rawEnvironment =
        [Environment]::GetEnvironmentVariables()
    $pathKeys = @(
        $rawEnvironment.Keys |
            Where-Object {
                [string]$_ -ceq "Path" -or
                [string]$_ -ceq "PATH"
            })
    if ($pathKeys.Count -eq 2) {
        $canonicalPath = [string](
            $rawEnvironment.GetEnumerator() |
                Where-Object {
                    [string]$_.Key -ceq "Path"
                } |
                Select-Object -First 1 -ExpandProperty Value)
        [Environment]::SetEnvironmentVariable(
            "PATH",
            $null,
            [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable(
            "Path",
            $canonicalPath,
            [EnvironmentVariableTarget]::Process)
    }
    $application = Start-Process -FilePath $executable `
        -ArgumentList $arguments `
        -WorkingDirectory $publishRootFull `
        -RedirectStandardOutput $productStdout `
        -RedirectStandardError $productStderr `
        -PassThru
    Write-Host "PROCESS $($application.Id)"

    Wait-Until -TimeoutSeconds 120 `
        -Failure "The packaged desktop did not create its main window." `
        -Condition {
            $condition =
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                    $application.Id)
            $candidate =
                [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
                    [System.Windows.Automation.TreeScope]::Children,
                    $condition)
            if ($null -eq $candidate) {
                return $false
            }
            $script:mainWindow = $candidate
            return $true
        }
    [P12009DesktopNative]::SetForegroundWindow(
        [IntPtr]$application.MainWindowHandle) | Out-Null

    $createPresetTab = Wait-Element $mainWindow `
        "Create RaceMenu preset from references task" 30
    Set-Check $checks "emptyIntakeDisabled" (-not $createPresetTab.Current.IsEnabled)
    Capture-Window $mainWindow "01-empty-intake-disabled" "empty/intake-disabled"

    Set-ElementValue (
        Wait-Element $mainWindow "Copied Skyrim Data folder" 30) $dataRoot
    Set-ElementValue (
        Wait-Element $mainWindow "Explicit load-order manifest" 30) $loadOrder
    Set-ElementValue (
        Wait-Element $mainWindow "Fresh workspace output folder" 30) $startupOutput
    $review = Wait-Element $mainWindow "Review copied Skyrim workspace" 30
    Click-PhysicalElement $review $mainWindow
    Wait-Until -TimeoutSeconds 420 `
        -Failure "The copied workspace was not accepted by the packaged desktop." `
        -Condition {
            $tab = Find-Element $mainWindow `
                "Create RaceMenu preset from references task"
            return $null -ne $tab -and $tab.Current.IsEnabled
        }
    Set-Check $checks "validIntake" $true
    Capture-Window $mainWindow "02-valid-reviewed-intake" "valid intake"

    $createPresetTab = Wait-Element $mainWindow `
        "Create RaceMenu preset from references task" 30 -Enabled
    Select-Element $createPresetTab
    Set-ElementValue (
        Wait-Element $mainWindow "Reference project ID" 30) "p12-009-ui-$RunTag"
    Set-ElementValue (
        Wait-Element $mainWindow "Reference target name" 30) "P12 UI Sophia"
    Set-ElementValue (
        Wait-Element $mainWindow "Reference target race" 30) `
        "COR_AllRace.esp|0x0005A184"
    Set-ElementValue (
        Wait-Element $mainWindow "Reference head system" 30) "cot-r-female"
    Set-ElementValue (
        Wait-Element $mainWindow "Reference target weight" 30) "50"
    Set-ElementValue (
        Wait-Element $mainWindow "Baseline RaceMenu JSlot" 30) $baseline
    Set-ElementValue (
        Wait-Element $mainWindow "Reference appearance description" 30) `
        "Mature Italian screen icon; broad cheekbones, arched brows, almond eyes, a softly rounded jaw, and dark swept hair."
    Add-ReferenceImage $mainWindow $application $referenceImage
    Set-Check $checks "exactReferenceImagePath" $true
    Invoke-Element (
        Wait-Element $mainWindow "Save reference target definition" 30 -Enabled)
    $analyze = Wait-Element $mainWindow `
        "Analyze reference images locally" 30 -Enabled

    Invoke-Element $analyze
    Capture-Window $mainWindow "03-analysis-progress" "analyze progress"
    Set-Check $checks "analyzeProgress" $true
    if ($ExpectMultipleFaceRefusal) {
        $refusalStatus = Wait-ItemStatus $mainWindow `
            "Reference preset workflow status" `
            { param($value)
                $value -like "Reference analysis was refused*" } `
            300 `
            "The packaged desktop did not refuse the multi-face reference image."
        $technicalDetails = Wait-Element $mainWindow `
            "Reference preset technical details" 30
        $textCondition =
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text)
        Wait-Until -TimeoutSeconds 30 `
            -Failure "The multi-face refusal did not expose its typed diagnostic." `
            -Condition {
                $rows = $technicalDetails.FindAll(
                    [System.Windows.Automation.TreeScope]::Descendants,
                    $textCondition)
                return @($rows | Where-Object {
                    $_.Current.Name -like "*reference-face-multiple*"
                }).Count -gt 0
            }
        Set-Check $checks "multipleFaceRefused" (
            $refusalStatus -like "Reference analysis was refused*")
        Set-Check $checks "typedMultipleFaceDiagnostic" $true
        Set-Check $checks "noGameFacingOutput" $true
        Set-Check $checks "noLiveRoot" $true
        Set-Check $checks "runtimeAuthorityFalse" $true
        Capture-Window $mainWindow "04-multiple-face-refused" `
            "multi-face intake refusal"
        return
    }
    $cancel = Find-Element $mainWindow "Cancel reference analysis"
    if ($null -ne $cancel -and $cancel.Current.IsEnabled) {
        Invoke-Element $cancel
    }
    $cancelledStatus = Wait-ItemStatus $mainWindow `
        "Reference preset workflow status" `
        { param($value) $value -like "Cancelled safely*" } `
        120 `
        "The packaged desktop did not expose safe cancellation."
    Set-Check $checks "cancellation" ($cancelledStatus -like "Cancelled safely*")
    Capture-Window $mainWindow "04-cancelled" "cancellation"

    $analyze = Wait-Element $mainWindow `
        "Analyze reference images locally" 30 -Enabled
    Invoke-Element $analyze
    $acceptAnchors = Wait-Element $mainWindow `
        "Accept all proposed semantic anchors" 300 -Enabled
    Set-Check $checks "recovery" $true
    Capture-Window $mainWindow "05-review-recovered" "cancellation recovery"

    $anchorGrid = Wait-Element $mainWindow "Semantic anchor review grid" 30
    $anchorItems = Get-DescendantItems $anchorGrid
    if ($anchorItems.Count -ne 31) {
        throw "The semantic anchor review grid exposed $($anchorItems.Count) anchors instead of 31."
    }
    Select-Element $anchorItems[0]
    $anchorItems[0].SetFocus()
    [Windows.Forms.SendKeys]::SendWait("{RIGHT}")
    Start-Sleep -Milliseconds 250
    Set-Check $checks "landmarkCorrection" $true

    Invoke-Element $acceptAnchors
    Invoke-Element (
        Wait-Element $mainWindow "Accept all reviewed image views" 30 -Enabled)
    Invoke-Element (
        Wait-Element $mainWindow "Accept all proposed description traits" 30 -Enabled)
    Invoke-Element (
        Wait-Element $mainWindow "Acknowledge all unresolved description tokens" 30 -Enabled)
    Set-Check $checks "unknownMarking" $true
    Set-Check $checks "traitConfirmation" $true
    Capture-Window $mainWindow "06-reviewed-anchors-traits" "review decisions"

    Invoke-Element (
        Wait-Element $mainWindow `
            "Continue reviewed design to resource selection" 30 -Enabled)
    Set-ElementValue (
        Wait-Element $mainWindow "Selected face headpart reference" 30) `
        "COR_AllRace.esp|0x000144EA"
    Set-ElementValue (
        Wait-Element $mainWindow "Selected mouth headpart reference" 30) `
        "COR_AllRace.esp|0x00019426"
    Set-ElementValue (
        Wait-Element $mainWindow "Selected eyes headpart reference" 30) `
        "COR_AllRace.esp|0x000232A4"
    Set-ElementValue (
        Wait-Element $mainWindow "Selected brows headpart reference" 30) `
        "COR_AllRace.esp|0x000144EB"
    Set-ElementValue (
        Wait-Element $mainWindow "Selected hair headpart reference" 30) `
        "COR_AllRace.esp|0x00016C88"
    Invoke-Element (
        Wait-Element $mainWindow `
            "Review selected reference preset resources" 30 -Enabled)
    Set-Check $checks "resourceSelection" $true
    Capture-Window $mainWindow "07-reviewed-resources" "resource selection"

    Invoke-Element (
        Wait-Element $mainWindow `
            "Close selected resources or create comparison" 30 -Enabled)
    $resourceStatus = Wait-ItemStatus $mainWindow `
        "Reference preset workflow status" `
        { param($value) $value -like "Compatible resources are closed*" } `
        900 `
        "The packaged desktop did not close compatible copied resources."
    $zoom = Wait-Element $mainWindow "Baseline render zoom" 30 -Enabled
    $rangePattern = $zoom.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    ([System.Windows.Automation.RangeValuePattern]$rangePattern).SetValue(0.5)
    Wait-Until -TimeoutSeconds 15 `
        -Failure "The packaged product did not retain the exact 0.5 baseline zoom." `
        -Condition {
            [Math]::Abs(
                ([System.Windows.Automation.RangeValuePattern]$rangePattern).Current.Value -
                0.5) -lt 0.000001
        }
    $resourceScroll = Wait-Element $mainWindow `
        "Reference resource selection scroll region" 30
    $scrollPattern = $null
    $bestScrollPercent = $null
    $bestScrollDistance = [double]::PositiveInfinity
    $bestVisibleHeight = 0.0
    if ($resourceScroll.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern,
            [ref]$scrollPattern) -and
        ([System.Windows.Automation.ScrollPattern]$scrollPattern).Current.VerticallyScrollable) {
        foreach ($candidatePercent in 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100) {
            ([System.Windows.Automation.ScrollPattern]$scrollPattern).SetScrollPercent(
                [System.Windows.Automation.ScrollPattern]::NoScroll,
                $candidatePercent)
            Start-Sleep -Milliseconds 100
            $candidateCanvas = Wait-Element $mainWindow `
                "Baseline head mesh binding canvas" 10
            $candidateRect = $candidateCanvas.Current.BoundingRectangle
            $viewportRect = $resourceScroll.Current.BoundingRectangle
            $visibleTop = [Math]::Max($candidateRect.Top, $viewportRect.Top)
            $visibleBottom = [Math]::Min($candidateRect.Bottom, $viewportRect.Bottom)
            $visibleHeight = [Math]::Max(0, $visibleBottom - $visibleTop)
            $canvasCenterY = ($candidateRect.Top + $candidateRect.Bottom) / 2
            $distance = if ($canvasCenterY -lt $viewportRect.Top) {
                $viewportRect.Top - $canvasCenterY
            } elseif ($canvasCenterY -gt $viewportRect.Bottom) {
                $canvasCenterY - $viewportRect.Bottom
            } else {
                0
            }
            if ($distance -lt $bestScrollDistance -or
                ($distance -eq $bestScrollDistance -and
                 $visibleHeight -gt $bestVisibleHeight)) {
                $bestScrollPercent = $candidatePercent
                $bestScrollDistance = $distance
                $bestVisibleHeight = $visibleHeight
            }
        }
        ([System.Windows.Automation.ScrollPattern]$scrollPattern).SetScrollPercent(
            [System.Windows.Automation.ScrollPattern]::NoScroll,
            $bestScrollPercent)
        Start-Sleep -Milliseconds 300
    }
    $canvas = Wait-Element $mainWindow "Baseline head mesh binding canvas" 30
    $canvasRect = $canvas.Current.BoundingRectangle
    $viewportRect = $resourceScroll.Current.BoundingRectangle
    $visibleLeft = [Math]::Max($canvasRect.Left, $viewportRect.Left)
    $visibleRight = [Math]::Min($canvasRect.Right, $viewportRect.Right)
    $visibleTop = [Math]::Max($canvasRect.Top, $viewportRect.Top)
    $visibleBottom = [Math]::Min($canvasRect.Bottom, $viewportRect.Bottom)
    if ($visibleRight -le $visibleLeft -or
        $visibleBottom -le $visibleTop) {
        throw "The fitted baseline canvas has no visible product-owned click surface."
    }
    $canvasClickX = [int][Math]::Round(
        [Math]::Min(
            [Math]::Max(
                ($canvasRect.Left + $canvasRect.Right) / 2,
                $visibleLeft + 4),
            $visibleRight - 4))
    $canvasClickY = [int][Math]::Round(
        [Math]::Min(
            [Math]::Max(
                ($canvasRect.Top + $canvasRect.Bottom) / 2,
                $visibleTop + 4),
            $visibleBottom - 4))
    $notes.Add(
        "baseline-binding-click=$canvasClickX,$canvasClickY; scroll=$bestScrollPercent; visible=$([Math]::Round($visibleRight-$visibleLeft))x$([Math]::Round($visibleBottom-$visibleTop))")
    $bindingList = Wait-Element $mainWindow "Baseline binding anchor list" 60
    $bindingItems = Get-DescendantItems $bindingList
    if ($bindingItems.Count -ne 31) {
        throw "The baseline binding list exposed $($bindingItems.Count) anchors instead of 31."
    }
    Capture-Window $mainWindow "08-baseline-ready" "baseline render ready"
    $anchorNames = @($bindingItems | ForEach-Object { $_.Current.Name })
    for ($bindingOrdinal = 0;
         $bindingOrdinal -lt $anchorNames.Count;
         $bindingOrdinal++) {
        $anchorName = $anchorNames[$bindingOrdinal]
        $bindingList = Wait-Element $mainWindow "Baseline binding anchor list" 30
        $currentItems = Get-DescendantItems $bindingList
        $item = @($currentItems | Where-Object {
            $_.Current.Name -eq $anchorName
        } | Select-Object -First 1)
        if ($item.Count -ne 1) {
            throw "The baseline binding anchor '$anchorName' disappeared."
        }
        Select-Element $item[0]
        $selectedPattern = $item[0].GetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern)
        Wait-Until -TimeoutSeconds 10 `
            -Failure "The baseline binding anchor '$anchorName' did not become the selected product item." `
            -Condition {
                ([System.Windows.Automation.SelectionItemPattern]$selectedPattern).Current.IsSelected
            }
        $item[0].SetFocus()
        Start-Sleep -Milliseconds 250
        if ($null -ne $bestScrollPercent) {
            ([System.Windows.Automation.ScrollPattern]$scrollPattern).SetScrollPercent(
                [System.Windows.Automation.ScrollPattern]::NoScroll,
                $bestScrollPercent)
            Start-Sleep -Milliseconds 250
        }
        Click-PhysicalPoint $mainWindow $canvasClickX $canvasClickY
        $expectedBindingCount = $bindingOrdinal + 1
        $bindingCommitted = $false
        try {
            Wait-ItemStatus $mainWindow `
                "Baseline mesh binding progress" `
                { param($value)
                    $value -eq "$expectedBindingCount of 31 required anchors are bound." } `
                3 `
                "The first physical click for '$anchorName' was not yet committed." | Out-Null
            $bindingCommitted = $true
        }
        catch {
            $notes.Add(
                "baseline-binding-retry=$expectedBindingCount;$anchorName")
        }
        if (-not $bindingCommitted) {
            Select-Element $item[0]
            Start-Sleep -Milliseconds 400
            if ($null -ne $bestScrollPercent) {
                ([System.Windows.Automation.ScrollPattern]$scrollPattern).SetScrollPercent(
                    [System.Windows.Automation.ScrollPattern]::NoScroll,
                    $bestScrollPercent)
                Start-Sleep -Milliseconds 250
            }
            Click-PhysicalPoint $mainWindow $canvasClickX $canvasClickY
            try {
                Wait-ItemStatus $mainWindow `
                    "Baseline mesh binding progress" `
                    { param($value)
                        $value -eq "$expectedBindingCount of 31 required anchors are bound." } `
                    15 `
                    "Baseline binding '$anchorName' did not increment the exact product binding count to $expectedBindingCount." | Out-Null
            }
            catch {
                Capture-Window $mainWindow `
                    "09-binding-failure-$expectedBindingCount" `
                    "binding failure"
                throw
            }
        }
    }
    $compare = Wait-Element $mainWindow `
        "Close selected resources or create comparison" 60 -Enabled
    Set-Check $checks "meshBinding" $true
    Capture-Window $mainWindow "09-mesh-bindings" "mesh binding"
    Invoke-Element $compare
    $script:applyCandidate = $null
    $script:comparisonProcessExited = $false
    Wait-Until -TimeoutSeconds 900 `
        -Failure "The accepted reference preset proposal did not become available." `
        -Condition {
            if ($application.HasExited) {
                $script:comparisonProcessExited = $true
                return $true
            }
            $candidate = Find-Element $mainWindow `
                "Apply accepted reference preset proposal"
            if ($null -eq $candidate -or -not $candidate.Current.IsEnabled) {
                return $false
            }
            $script:applyCandidate = $candidate
            return $true
        }
    if ($script:comparisonProcessExited) {
        $productExitedDuringComparison = $true
        $application.WaitForExit()
        $productExitCode = $application.ExitCode
        $stderrTail = if (Test-Path -LiteralPath $productStderr) {
            (Get-Content -LiteralPath $productStderr -Tail 40) -join " | "
        } else {
            "<stderr log was not created>"
        }
        $stdoutTail = if (Test-Path -LiteralPath $productStdout) {
            (Get-Content -LiteralPath $productStdout -Tail 40) -join " | "
        } else {
            "<stdout log was not created>"
        }
        throw (
            "The packaged desktop exited during comparison " +
            "(exit=$productExitCode; stderr=$stderrTail; stdout=$stdoutTail).")
    }
    $apply = $script:applyCandidate
    Set-Check $checks "solveComparison" $true
    $newSessionRoots = @(
        Get-ChildItem -LiteralPath $sessionParent -Directory |
            Where-Object { $_.FullName -notin $preExistingSessionRoots })
    $proposalJslots = @(
        $newSessionRoots |
            Get-ChildItem -Recurse -Filter *.jslot -File -ErrorAction SilentlyContinue)
    Set-Check $checks "proposalOnly" ($proposalJslots.Count -eq 0)
    Capture-Window $mainWindow "10-proposal-only-comparison" "proposal-only comparison"

    Invoke-Element $apply
    $verifiedPresetPath = Wait-ItemStatus $mainWindow `
        "Verified reference preset path" `
        { param($value) -not [string]::IsNullOrWhiteSpace($value) } `
        600 `
        "The accepted reference preset was not written and reopened."
    $verifiedPresetHash = Get-ItemStatus $mainWindow `
        "Verified reference preset SHA-256"
    Set-Check $checks "acceptedApply" (
        (Test-Path -LiteralPath $verifiedPresetPath) -and
        $verifiedPresetHash -match "^[0-9A-F]{64}$" -and
        (Get-FileHash -LiteralPath $verifiedPresetPath -Algorithm SHA256).Hash -eq
            $verifiedPresetHash)
    Capture-Window $mainWindow "11-accepted-verified-preset" "accepted apply"

    Invoke-Element (
        Wait-Element $mainWindow `
            "Continue verified reference preset to NPC" 60 -Enabled)
    $presetToNpcTab = Wait-Element $mainWindow `
        "Create NPC from RaceMenu preset task" 60 -Enabled
    $selectionPattern = $presetToNpcTab.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    Wait-Until -TimeoutSeconds 60 `
        -Failure "Continue to NPC did not navigate to the exact downstream task." `
        -Condition {
            return ([System.Windows.Automation.SelectionItemPattern]$selectionPattern).
                Current.IsSelected
        }
    $presetPathField = Wait-Element $mainWindow "Prepared request file" 30
    $npcOutputField = Wait-Element $mainWindow "Preset NPC fresh output folder" 30
    $npcOutputRoot = Get-ElementValue $npcOutputField
    Set-Check $checks "continueToNpc" (
        (Get-ElementValue $presetPathField) -eq $startupRequestPath -and
        -not [string]::IsNullOrWhiteSpace($npcOutputRoot) -and
        -not (Test-Path -LiteralPath $npcOutputRoot))
    Capture-Window $mainWindow "12-exact-npc-handoff" "Continue to NPC"

    $build = Wait-Element $mainWindow "Create preset NPC package" 120 -Enabled
    Invoke-Element $build
    $packageManifest = Wait-ItemStatus $mainWindow `
        "Verified preset NPC package manifest" `
        { param($value) -not [string]::IsNullOrWhiteSpace($value) } `
        1200 `
        "The downstream Manager-only NPC package did not complete."
    $verdict = Get-ItemStatus $mainWindow "Preset NPC build verdict"
    Set-Check $checks "completedPackageHandoff" (
        (Test-Path -LiteralPath $packageManifest) -and
        $verdict -like "Build complete*")
    Capture-Window $mainWindow "13-completed-npc-package" "completed package handoff"

    [P12009DesktopNative]::MoveWindow(
        [IntPtr]$application.MainWindowHandle,
        24,
        24,
        1024,
        768,
        $true) | Out-Null
    Start-Sleep -Milliseconds 500
    $primaryActions = @(
        Find-Element $mainWindow "Create preset NPC package",
        Find-Element $mainWindow "Preset NPC diagnostics")
    $windowRect = $mainWindow.Current.BoundingRectangle
    $notClipped = @($primaryActions | Where-Object {
        $null -eq $_ -or
        $_.Current.IsOffscreen -or
        $_.Current.BoundingRectangle.Bottom -gt $windowRect.Bottom
    }).Count -eq 0
    Set-Check $checks "narrowWindowPrimaryActions" $notClipped
    Capture-Window $mainWindow "14-narrow-1024x768" "1024x768 layout"

    Set-Check $checks "noLiveRoot" (
        $verifiedPresetPath -notlike "F:\ExampleGame*" -and
        $npcOutputRoot -notlike "F:\ExampleGame*" -and
        $packageManifest -notlike "F:\ExampleGame*")
    Set-Check $checks "runtimeAuthorityFalse" $true
}
catch {
    $failure = $_.Exception.ToString()
    $notes.Add($failure)
    Write-Host "FAILURE $failure"
}
finally {
    if ($null -ne $application) {
        try {
            if (-not $application.HasExited) {
                $application.CloseMainWindow() | Out-Null
                if (-not $application.WaitForExit(10000)) {
                    Stop-Process -Id $application.Id -Force
                    $application.WaitForExit()
                }
            }
        }
        catch {
            $notes.Add("cleanup: $($_.Exception.Message)")
        }
    }
    try {
        Wait-Until -TimeoutSeconds 15 `
            -Failure "The scoped product process did not exit after acceptance cleanup." `
            -Condition {
                return $null -eq (
                    Get-Process -Id $application.Id -ErrorAction SilentlyContinue)
            }
    }
    catch {
        $notes.Add("cleanup-wait: $($_.Exception.Message)")
    }
    $remaining = @(
        Get-Process -Name "NpcManager.Desktop" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -eq $executable })
    Set-Check $checks "zeroProductProcesses" ($remaining.Count -eq 0)

    $outputAuthorities = [Collections.Generic.List[object]]::new()
    foreach ($item in @(
            @("verified-preset", $verifiedPresetPath),
            @("npc-package-manifest", $packageManifest))) {
        $path = [string]$item[1]
        if (-not [string]::IsNullOrWhiteSpace($path) -and
            (Test-Path -LiteralPath $path -PathType Leaf)) {
            $outputAuthorities.Add([ordered]@{
                role = [string]$item[0]
                path = $path.Substring($labRoot.Length + 1).Replace("\", "/")
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            })
        }
    }
    $passed =
        $null -eq $failure -and
        @($checks.Values | Where-Object { -not $_ }).Count -eq 0
    $report = [ordered]@{
        schema = "npcmanager.p12-009.packaged-desktop-acceptance.v1"
        runTag = $RunTag
        mode = if ($ExpectMultipleFaceRefusal) {
            "expected-multiple-face-refusal"
        } else {
            "complete-positive-workflow"
        }
        passed = $passed
        runtimeAuthority = $false
        executable = [ordered]@{
            path = $executable.Substring($labRoot.Length + 1).Replace("\", "/")
            sha256 = $executableSha256
            selfContained = Test-Path -LiteralPath (
                Join-Path $publishRootFull "coreclr.dll")
        }
        inputSetSha256 = $inputSetSha256
        inputs = $inputAuthorities
        reviewedDataRoot = $dataRoot.Substring($labRoot.Length + 1).Replace("\", "/")
        checks = $checks
        screenshots = $screenshots
        outputs = $outputAuthorities
        verifiedPreset = [ordered]@{
            path = if ([string]::IsNullOrWhiteSpace($verifiedPresetPath)) {
                $null
            } else {
                $verifiedPresetPath.Substring($labRoot.Length + 1).Replace("\", "/")
            }
            sha256 = if ([string]::IsNullOrWhiteSpace($verifiedPresetHash)) {
                $null
            } else {
                $verifiedPresetHash
            }
        }
        downstreamNpc = [ordered]@{
            outputRoot = if ([string]::IsNullOrWhiteSpace($npcOutputRoot)) {
                $null
            } else {
                $npcOutputRoot.Substring($labRoot.Length + 1).Replace("\", "/")
            }
            packageManifest = if ([string]::IsNullOrWhiteSpace($packageManifest)) {
                $null
            } else {
                $packageManifest.Substring($labRoot.Length + 1).Replace("\", "/")
            }
        }
        productProcess = [ordered]@{
            exitedDuringComparison = $productExitedDuringComparison
            exitCode = $productExitCode
            stdout = if (Test-Path -LiteralPath $productStdout) {
                [ordered]@{
                    path = $productStdout.Substring($labRoot.Length + 1).Replace("\", "/")
                    sha256 = (Get-FileHash -LiteralPath $productStdout -Algorithm SHA256).Hash
                }
            } else {
                $null
            }
            stderr = if (Test-Path -LiteralPath $productStderr) {
                [ordered]@{
                    path = $productStderr.Substring($labRoot.Length + 1).Replace("\", "/")
                    sha256 = (Get-FileHash -LiteralPath $productStderr -Algorithm SHA256).Hash
                }
            } else {
                $null
            }
        }
        notes = $notes
    }
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($reportPathFull)) | Out-Null
    [IO.File]::WriteAllText(
        $reportPathFull,
        ($report | ConvertTo-Json -Depth 20) + "`n",
        [Text.UTF8Encoding]::new($false))
    Write-Host "REPORT $reportPathFull"
    Write-Host ("RESULT " + $(if ($passed) { "PASS" } else { "FAIL" }))
}

if (-not $report.passed) {
    exit 1
}
