param(
    [Parameter(Mandatory = $true)][string]$ProposalPath,
    [Parameter(Mandatory = $true)][string]$ExpectedProposalSha256,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$ExpectedSourceOptionsSha256,
    [Parameter(Mandatory = $true)][string]$ExpectedDataRoot,
    [Parameter(Mandatory = $true)][string]$ExpectedPlugin,
    [Parameter(Mandatory = $true)][string]$ExpectedNpc,
    [Parameter(Mandatory = $true)][string]$ExpectedRace,
    [Parameter(Mandatory = $true)][string]$ExpectedDdsSha256,
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [switch]$ExpectFailure
)

$ErrorActionPreference = "Stop"
$checks = New-Object System.Collections.Generic.List[object]
$reportFull = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $reportFull) {
    throw "Verification report already exists and will not be overwritten: $reportFull"
}

function Add-Check {
    param(
        [string]$Name,
        [object]$Expected,
        [object]$Actual,
        [bool]$Passed
    )
    $checks.Add([ordered]@{
        name = $Name
        expected = $Expected
        actual = $Actual
        passed = $Passed
    })
}

function Get-Hash {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Same-Text {
    param([object]$Left, [object]$Right)
    return [string]::Equals(
        [string]$Left,
        [string]$Right,
        [StringComparison]::OrdinalIgnoreCase)
}

function Read-U32 {
    param([byte[]]$Bytes, [int]$Offset)
    return [BitConverter]::ToUInt32($Bytes, $Offset)
}

$proposalFull = [IO.Path]::GetFullPath($ProposalPath)
$outputFull = [IO.Path]::GetFullPath($OutputRoot)
$optionsPath = Join-Path $outputFull "accepted-options.json"
$ddsPath = Join-Path $outputFull "facetint.dds"
$receiptPath = Join-Path $outputFull "options-to-facetint-receipt.json"
$proposal = $null
$options = $null
$receipt = $null

try {
    if (Test-Path -LiteralPath $proposalFull -PathType Leaf) {
        $proposal = Get-Content -Raw -LiteralPath $proposalFull |
            ConvertFrom-Json
    }
    if (Test-Path -LiteralPath $optionsPath -PathType Leaf) {
        $options = Get-Content -Raw -LiteralPath $optionsPath |
            ConvertFrom-Json
    }
    if (Test-Path -LiteralPath $receiptPath -PathType Leaf) {
        $receipt = Get-Content -Raw -LiteralPath $receiptPath |
            ConvertFrom-Json
    }
}
catch {
    Add-Check "JSON parse" "all three documents parse" $_.Exception.Message $false
}

$proposalHash = Get-Hash $proposalFull
Add-Check "proposal SHA-256" $ExpectedProposalSha256 $proposalHash `
    (Same-Text $ExpectedProposalSha256 $proposalHash)
Add-Check "proposal kind" "skyrim-chargen-options-production-proposal" `
    $proposal.artifactKind `
    ($null -ne $proposal -and $proposal.schemaVersion -eq "1" -and
        $proposal.artifactKind -eq "skyrim-chargen-options-production-proposal")
Add-Check "proposal source SHA-256" $ExpectedSourceOptionsSha256 `
    $proposal.sourceOptionsSha256 `
    ($null -ne $proposal -and
        (Same-Text $ExpectedSourceOptionsSha256 $proposal.sourceOptionsSha256))
Add-Check "proposal Data root" ([IO.Path]::GetFullPath($ExpectedDataRoot)) `
    $proposal.dataRoot `
    ($null -ne $proposal -and
        [string]::Equals(
            [IO.Path]::GetFullPath($ExpectedDataRoot),
            [IO.Path]::GetFullPath([string]$proposal.dataRoot),
            [StringComparison]::OrdinalIgnoreCase))
Add-Check "proposal plugin order" @($ExpectedPlugin) @($proposal.pluginOrder) `
    ($null -ne $proposal -and $proposal.pluginOrder.Count -eq 1 -and
        $proposal.pluginOrder[0] -eq $ExpectedPlugin)
Add-Check "proposal NPC" $ExpectedNpc $proposal.npc `
    ($null -ne $proposal -and (Same-Text $ExpectedNpc $proposal.npc))
Add-Check "proposal race" $ExpectedRace $proposal.expectedRace `
    ($null -ne $proposal -and (Same-Text $ExpectedRace $proposal.expectedRace))
Add-Check "proposal output root" $outputFull $proposal.outputRoot `
    ($null -ne $proposal -and
        [string]::Equals(
            $outputFull,
            [IO.Path]::GetFullPath([string]$proposal.outputRoot),
            [StringComparison]::OrdinalIgnoreCase))
Add-Check "proposal runtime authority" $false $proposal.runtimeAuthority `
    ($null -ne $proposal -and $proposal.runtimeAuthority -eq $false)

$outputFiles = if (Test-Path -LiteralPath $outputFull -PathType Container) {
    @(Get-ChildItem -LiteralPath $outputFull -File | Sort-Object Name |
        Select-Object -ExpandProperty Name)
}
else { @() }
Add-Check "exact output surface" `
    @("accepted-options.json", "facetint.dds", "options-to-facetint-receipt.json") `
    $outputFiles `
    ($outputFiles.Count -eq 3 -and
        $outputFiles[0] -eq "accepted-options.json" -and
        $outputFiles[1] -eq "facetint.dds" -and
        $outputFiles[2] -eq "options-to-facetint-receipt.json")

$optionsHash = Get-Hash $optionsPath
Add-Check "canonical options admitted values" `
    "Skyrim SE uniform 512 BC3/uncompressed/BC5, no TGA, no overlays" `
    $(if ($null -eq $options) { $null } else {
        "$($options.edition)|$($options.perLayerResolution)|$($options.diffuseResolution)|$($options.diffuseCompression)|$($options.normalCompression)|$($options.specularCompression)|$($options.generateTga)|$($options.bakeSseRaceMenuOverlays)"
    }) `
    ($null -ne $options -and $options.schemaVersion -eq "1" -and
        $options.edition -eq "skyrimse" -and
        $options.perLayerResolution -eq $false -and
        $options.diffuseResolution -eq "r512" -and
        $options.normalResolution -eq "r512" -and
        $options.specularResolution -eq "r512" -and
        $options.diffuseCompression -eq "bc3" -and
        $options.normalCompression -eq "uncompressed" -and
        $options.specularCompression -eq "bc5" -and
        $options.generateTga -eq $false -and
        $options.bakeSseRaceMenuOverlays -eq $false)
Add-Check "canonical conventions" "linear/raw/overPrev/gimp/red" `
    $(if ($null -eq $options) { $null } else {
        "$($options.convention.diffuse.workingSpace)|$($options.convention.diffuse.maskConversion)|$($options.convention.diffuse.framework)|$($options.convention.diffuse.softLight)|$($options.convention.diffuse.maskChannel)"
    }) `
    ($null -ne $options -and
        $options.convention.diffuse.workingSpace -eq "linear" -and
        $options.convention.diffuse.compositeSpace -eq "linear" -and
        $options.convention.diffuse.sourceSpace -eq "linear" -and
        $options.convention.diffuse.outputSpace -eq "linear" -and
        $options.convention.diffuse.maskConversion -eq "raw" -and
        $options.convention.diffuse.framework -eq "overPrev" -and
        $options.convention.diffuse.softLight -eq "gimp" -and
        $options.convention.diffuse.maskChannel -eq "r")
Add-Check "canonical order" "RaceOrder asc / OverlayIndex asc / positional" `
    $(if ($null -eq $options) { $null } else {
        "$($options.tintSort.tintRules[0].key)|$($options.tintSort.tintRules[0].descending)|$($options.tintSort.swapRules[0].key)|$($options.tintSort.swapRules[0].descending)|$($options.tintSort.skinTonePlacement)"
    }) `
    ($null -ne $options -and $options.tintSort.tintRules.Count -eq 1 -and
        $options.tintSort.tintRules[0].key -eq 0 -and
        $options.tintSort.tintRules[0].descending -eq $false -and
        $options.tintSort.swapRules.Count -eq 1 -and
        $options.tintSort.swapRules[0].key -eq 0 -and
        $options.tintSort.swapRules[0].descending -eq $false -and
        $options.tintSort.skinTonePlacement -eq "positional")

Add-Check "receipt kind" "skyrim-chargen-options-native-facetint-receipt" `
    $receipt.artifactKind `
    ($null -ne $receipt -and $receipt.schemaVersion -eq "1" -and
        $receipt.artifactKind -eq
            "skyrim-chargen-options-native-facetint-receipt")
Add-Check "receipt options hash binding" $optionsHash `
    $receipt.optionsSha256.value `
    ($null -ne $receipt -and (Same-Text $optionsHash $receipt.optionsSha256.value))
Add-Check "receipt consumption contract" `
    "uniform-512-bc3-linear-red-mask-over-prev-race-order-constant-seed-no-overlays" `
    $receipt.consumptionContract `
    ($null -ne $receipt -and $receipt.consumptionContract -eq
        "uniform-512-bc3-linear-red-mask-over-prev-race-order-constant-seed-no-overlays")
$receiptNpc = if ($null -eq $receipt) { "" } else {
    "$($receipt.nativeFaceTint.npc.plugin.value)|$('0x{0:X8}' -f [uint32]$receipt.nativeFaceTint.npc.formId.value)"
}
$receiptRace = if ($null -eq $receipt) { "" } else {
    "$($receipt.nativeFaceTint.race.plugin.value)|$('0x{0:X8}' -f [uint32]$receipt.nativeFaceTint.race.formId.value)"
}
Add-Check "receipt native identity" "$ExpectedNpc / $ExpectedRace / female" `
    $(if ($null -eq $receipt) { $null } else {
        "$receiptNpc / $receiptRace / $($receipt.nativeFaceTint.sex)"
    }) `
    ($null -ne $receipt -and
        $receiptNpc -eq $ExpectedNpc -and
        $receiptRace -eq $ExpectedRace -and
        $receipt.nativeFaceTint.sex -eq "female")

$ddsHash = Get-Hash $ddsPath
$ddsBytes = if (Test-Path -LiteralPath $ddsPath -PathType Leaf) {
    [IO.File]::ReadAllBytes($ddsPath)
}
else { [byte[]]@() }
$ddsMagic = if ($ddsBytes.Length -ge 4) {
    [Text.Encoding]::ASCII.GetString($ddsBytes, 0, 4)
}
else { "" }
$ddsHeight = if ($ddsBytes.Length -ge 32) { Read-U32 $ddsBytes 12 } else { 0 }
$ddsWidth = if ($ddsBytes.Length -ge 32) { Read-U32 $ddsBytes 16 } else { 0 }
$ddsMips = if ($ddsBytes.Length -ge 32) { Read-U32 $ddsBytes 28 } else { 0 }
$ddsFourCc = if ($ddsBytes.Length -ge 88) {
    [Text.Encoding]::ASCII.GetString($ddsBytes, 84, 4)
}
else { "" }
Add-Check "DDS expected hash" $ExpectedDdsSha256 $ddsHash `
    (Same-Text $ExpectedDdsSha256 $ddsHash)
Add-Check "DDS receipt hash" $ddsHash $receipt.nativeFaceTint.outputSha256.value `
    ($null -ne $receipt -and
        (Same-Text $ddsHash $receipt.nativeFaceTint.outputSha256.value))
Add-Check "DDS header" "DDS / 512x512 / DXT5 / 10 mips / 349680 bytes" `
    "$ddsMagic / ${ddsWidth}x${ddsHeight} / $ddsFourCc / $ddsMips mips / $($ddsBytes.Length) bytes" `
    ($ddsMagic -eq "DDS " -and $ddsWidth -eq 512 -and
        $ddsHeight -eq 512 -and $ddsFourCc -eq "DXT5" -and
        $ddsMips -eq 10 -and $ddsBytes.Length -eq 349680)

$pluginPath = if ($null -ne $receipt) {
    [string]$receipt.pluginAuthorities[0].path.value
}
else { "" }
$pluginHash = Get-Hash $pluginPath
Add-Check "plugin authority" "$ExpectedPlugin / hash-bound" `
    "$($receipt.pluginAuthorities[0].plugin.value) / $pluginHash" `
    ($null -ne $receipt -and $receipt.pluginAuthorities.Count -eq 1 -and
        $receipt.pluginAuthorities[0].plugin.value -eq $ExpectedPlugin -and
        (Same-Text $pluginHash $receipt.pluginAuthorities[0].expectedSha256.value))
$maskPath = if ($null -ne $receipt) {
    [string]$receipt.maskAuthorities[0].providerPath.value
}
else { "" }
$maskHash = Get-Hash $maskPath
Add-Check "mask authority" "one exact loose mask" `
    "$($receipt.maskAuthorities[0].assetPath.value) / $maskHash" `
    ($null -ne $receipt -and $receipt.maskAuthorities.Count -eq 1 -and
        $receipt.maskAuthorities[0].providerKind -eq "loose" -and
        $receipt.maskAuthorities[0].assetPath.value -eq
            "textures/fixture/mask.dds" -and
        (Same-Text $maskHash $receipt.maskAuthorities[0].providerSha256.value) -and
        (Same-Text $maskHash $receipt.maskAuthorities[0].contentSha256.value))
Add-Check "all runtime authority flags" $false `
    "$($proposal.runtimeAuthority)|$($receipt.runtimeAuthority)|$($receipt.nativeFaceTint.runtimeAuthority)" `
    ($null -ne $proposal -and $null -ne $receipt -and
        $proposal.runtimeAuthority -eq $false -and
        $receipt.runtimeAuthority -eq $false -and
        $receipt.nativeFaceTint.runtimeAuthority -eq $false)

$failed = @($checks | Where-Object { -not $_.passed })
$success = if ($ExpectFailure) { $failed.Count -gt 0 } else { $failed.Count -eq 0 }
$verdict = if ($ExpectFailure -and $success) {
    "EXPECTED_FAIL"
}
elseif ($success) { "PASS" } else { "FAIL" }
$report = [ordered]@{
    schemaVersion = 1
    artifactKind = "sky-gui-022-independent-options-facetint-verification"
    verdict = $verdict
    expectFailure = [bool]$ExpectFailure
    proposalPath = $proposalFull
    outputRoot = $outputFull
    checkCount = $checks.Count
    mismatchCount = $failed.Count
    checks = $checks.ToArray()
    runtimeAuthority = $false
    protectedLiveRootTouched = $false
}
$json = $report | ConvertTo-Json -Depth 12
$reportParent = Split-Path -Parent $reportFull
if (-not (Test-Path -LiteralPath $reportParent -PathType Container)) {
    throw "Report parent does not exist: $reportParent"
}
[IO.File]::WriteAllText(
    $reportFull,
    $json + [Environment]::NewLine,
    (New-Object Text.UTF8Encoding($false)))
Write-Output $json
if (-not $success) { exit 1 }
