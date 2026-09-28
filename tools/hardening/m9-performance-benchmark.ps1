param(
    [string]$PackageVersion = '1.0.0-preview.3',
    [string]$PackageAcceptance = '05-reports\m8-package-acceptance-2026-07-18b.json',
    [string]$EvidenceStamp = '2026-07-18g'
)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $projectRoot 'tools\hardening\verify_package.py'
$packageRoot = Join-Path $projectRoot "04-packages\npcmanager-$PackageVersion"
$packageArchive = Join-Path $projectRoot "04-packages\npcmanager-$PackageVersion.zip"
$packageReport = Join-Path $projectRoot $PackageAcceptance
$fixtureRoot = Join-Path $projectRoot '03-builds\work\m9-facegeom-bound-evidence'
$previewManifest = Join-Path $projectRoot '03-builds\work\m6-preview-scene-evidence\scene.json'
$faceGenManifest = Join-Path $projectRoot '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$output = Join-Path $projectRoot "05-reports\m9-performance-benchmark-$EvidenceStamp.json"
$work = Join-Path $projectRoot "03-builds\work\m9-performance-benchmark-$EvidenceStamp"

foreach ($path in @($dotnet, $cli, $verifier, $packageRoot, $packageArchive, $packageReport,
        $previewManifest, $faceGenManifest, (Join-Path $fixtureRoot 'fallout4\Data'),
        (Join-Path $fixtureRoot 'skyrimse\Data'))) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required benchmark input was not found: $path" }
}
if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite existing output: $output" }
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Measure-Npcm([string[]]$arguments, [int]$iterations) {
    $measurements = @()
    for ($index = 0; $index -lt $iterations; $index++) {
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        & $dotnet $cli @arguments *> $null
        $timer.Stop()
        if ($LASTEXITCODE -ne 0) { throw "Benchmark command failed with exit ${LASTEXITCODE}: $($arguments -join ' ')" }
        $measurements += [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
    }
    return $measurements
}

function Measure-NpcmPeak([string[]]$arguments) {
    $processArguments = @($cli) + $arguments
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnet
    $startInfo.Arguments = (($processArguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start benchmark process: $($processArguments -join ' ')" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $peak = [int64]$process.WorkingSet64
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $process.HasExited) {
        $workingSet = [int64]$process.WorkingSet64
        if ($workingSet -gt $peak) { $peak = $workingSet }
        Start-Sleep -Milliseconds 5
    }
    $timer.Stop()
    $process.Refresh()
    if ($process.WorkingSet64 -gt $peak) { $peak = [int64]$process.WorkingSet64 }
    $stdout.Result | Out-Null
    $stderrText = $stderr.Result
    if ($process.ExitCode -ne 0) {
        throw "Benchmark process failed with exit $($process.ExitCode): $stderrText"
    }
    return [ordered]@{
        elapsedMs = [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
        peakWorkingSetBytes = $peak
    }
}

function Measure-Python([string[]]$arguments, [int]$iterations) {
    $measurements = @()
    for ($index = 0; $index -lt $iterations; $index++) {
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        & python @arguments *> $null
        $timer.Stop()
        if ($LASTEXITCODE -ne 0) { throw "Benchmark verifier failed with exit ${LASTEXITCODE}: $($arguments -join ' ')" }
        $measurements += [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
    }
    return $measurements
}

function Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return $sorted[$middle] }
    $upper = $middle
    return [math]::Round(($sorted[$upper - 1] + $sorted[$upper]) / 2, 3)
}

$fo4Case = Join-Path $fixtureRoot 'fallout4'
$sseCase = Join-Path $fixtureRoot 'skyrimse'
$fo4Data = Join-Path $fo4Case 'Data'
$sseData = Join-Path $sseCase 'Data'
$fo4Plugin = 'P13BoundFO4.esp'
$ssePlugin = 'P13BoundSSE.esp'
$fo4Morphs = Join-Path $fo4Case 'morphs.json'
$sseMorphs = Join-Path $sseCase 'morphs.json'
$batchManifest = Join-Path $work 'facegen-batch.json'
$batchJson = (@{ schemaVersion = 1; edition = 'fallout4'; manifests = @($faceGenManifest) } |
    ConvertTo-Json -Compress)
[IO.File]::WriteAllText($batchManifest, $batchJson, [Text.UTF8Encoding]::new($false))

$capabilities = Measure-Npcm @('capabilities', '--json') 5
$assetIndex = [ordered]@{}
$preflight = [ordered]@{}
$providerResolution = [ordered]@{}
$geomBound = [ordered]@{}
$previewRender = @()
$faceGenBatch = @()
$faceGenBatchPeakWorkingSetBytes = @()
foreach ($edition in @('fallout4', 'skyrimse')) {
    $data = if ($edition -eq 'fallout4') { $fo4Data } else { $sseData }
    $plugin = if ($edition -eq 'fallout4') { $fo4Plugin } else { $ssePlugin }
    $morphs = if ($edition -eq 'fallout4') { $fo4Morphs } else { $sseMorphs }
    New-Item -ItemType Directory -Path (Join-Path $work $edition) -Force | Out-Null
    $assetMeasurements = @()
    $preflightMeasurements = @()
    $providerMeasurements = @()
    $geomMeasurements = @()
    for ($index = 0; $index -lt 3; $index++) {
        $assetOutput = Join-Path $work "$edition\asset-index-$index.json"
        $assetMeasurements += Measure-Npcm @('assets', 'index', '--edition', $edition, '--data-root', $data,
            '--output', $assetOutput, '--json') 1

        $preflightOutput = Join-Path $work "$edition\preflight-$index"
        New-Item -ItemType Directory -Path $preflightOutput -Force | Out-Null
        $preflightMeasurements += Measure-Npcm @('workspace', 'preflight', '--game', $edition,
            '--data-root', $data, '--output-root', $preflightOutput, '--json') 1

        $providerMeasurements += Measure-Npcm @('facegen', 'resolve-providers', '--game', $edition,
            '--data-root', $data, '--plugins', $plugin, '--npc', '0x800', '--json') 1

        $geomOutput = Join-Path $work "$edition\geom-$index"
        New-Item -ItemType Directory -Path $geomOutput -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $geomOutput "Meshes\Actors\Character\FaceGenData\FaceGeom\$plugin") -Force | Out-Null
        $geomMeasurements += Measure-Npcm @('facegen', 'build-geom-bound', '--game', $edition,
            '--data-root', $data, '--plugins', $plugin, '--npc', '0x800', '--output-root', $geomOutput,
            '--morphs', "@$morphs", '--json') 1
    }
    $assetIndex[$edition] = $assetMeasurements
    $preflight[$edition] = $preflightMeasurements
    $providerResolution[$edition] = $providerMeasurements
    $geomBound[$edition] = $geomMeasurements
}

for ($index = 0; $index -lt 3; $index++) {
    $previewOutput = Join-Path $work "preview-$index.json"
    $previewRender += Measure-Npcm @('preview', 'render', '--edition', 'fallout4', '--manifest', $previewManifest,
        '--output', $previewOutput, '--json') 1

    $batchOutput = Join-Path $work "facegen-batch-$index.json"
    $batchSample = Measure-NpcmPeak @('facegen', 'bake-all', '--edition', 'fallout4', '--manifests', $batchManifest,
        '--output', $batchOutput, '--json')
    $faceGenBatch += $batchSample.elapsedMs
    $faceGenBatchPeakWorkingSetBytes += $batchSample.peakWorkingSetBytes
}

$packageVerification = Measure-Python @($verifier, '--package-root', $packageRoot, '--archive',
    $packageArchive, '--report', $packageReport) 3

$budgets = [ordered]@{
    cliStartupColdMs = 750
    assetIndexColdMs = 2000
    workspacePreflightColdMs = 250
    faceGenProviderResolutionColdMs = 3000
    faceGeomProviderBoundColdMs = 30000
    previewRenderColdMs = 2000
    faceGenBatchColdMs = 5000
    faceGenBatchPeakWorkingSetBytes = 536870912
    packageVerificationColdMs = 10000
}
$summary = [ordered]@{
    cliStartupCold = $capabilities[0]
    cliStartupWarmMedian = Median $capabilities[1..($capabilities.Count - 1)]
    assetIndexCold = [ordered]@{}
    assetIndexWarmMedian = [ordered]@{}
    workspacePreflightCold = [ordered]@{}
    workspacePreflightWarmMedian = [ordered]@{}
    faceGenProviderResolutionCold = [ordered]@{}
    faceGenProviderResolutionWarmMedian = [ordered]@{}
    faceGeomProviderBoundCold = [ordered]@{}
    faceGeomProviderBoundWarmMedian = [ordered]@{}
    previewRenderCold = $previewRender[0]
    previewRenderWarmMedian = Median $previewRender[1..($previewRender.Count - 1)]
    faceGenBatchCold = $faceGenBatch[0]
    faceGenBatchWarmMedian = Median $faceGenBatch[1..($faceGenBatch.Count - 1)]
    faceGenBatchPeakWorkingSetBytesMax = ($faceGenBatchPeakWorkingSetBytes | Measure-Object -Maximum).Maximum
    faceGenBatchPeakWorkingSetBytesMedian = Median ([double[]]$faceGenBatchPeakWorkingSetBytes)
    packageVerificationCold = $packageVerification[0]
    packageVerificationWarmMedian = Median $packageVerification[1..($packageVerification.Count - 1)]
}
foreach ($edition in @('fallout4', 'skyrimse')) {
    $summary.assetIndexCold[$edition] = $assetIndex[$edition][0]
    $summary.assetIndexWarmMedian[$edition] = Median $assetIndex[$edition][1..2]
    $summary.workspacePreflightCold[$edition] = $preflight[$edition][0]
    $summary.workspacePreflightWarmMedian[$edition] = Median $preflight[$edition][1..2]
    $summary.faceGenProviderResolutionCold[$edition] = $providerResolution[$edition][0]
    $summary.faceGenProviderResolutionWarmMedian[$edition] = Median $providerResolution[$edition][1..2]
    $summary.faceGeomProviderBoundCold[$edition] = $geomBound[$edition][0]
    $summary.faceGeomProviderBoundWarmMedian[$edition] = Median $geomBound[$edition][1..2]
}

$status = 'PASS'
if ($summary.cliStartupCold -gt $budgets.cliStartupColdMs -or
    $summary.previewRenderCold -gt $budgets.previewRenderColdMs -or
    $summary.faceGenBatchCold -gt $budgets.faceGenBatchColdMs -or
    $summary.faceGenBatchPeakWorkingSetBytesMax -gt $budgets.faceGenBatchPeakWorkingSetBytes -or
    $summary.packageVerificationCold -gt $budgets.packageVerificationColdMs) { $status = 'FAIL' }
foreach ($edition in @('fallout4', 'skyrimse')) {
    if ($summary.assetIndexCold[$edition] -gt $budgets.assetIndexColdMs -or
        $summary.workspacePreflightCold[$edition] -gt $budgets.workspacePreflightColdMs -or
        $summary.faceGenProviderResolutionCold[$edition] -gt $budgets.faceGenProviderResolutionColdMs -or
        $summary.faceGeomProviderBoundCold[$edition] -gt $budgets.faceGeomProviderBoundColdMs) { $status = 'FAIL' }
}

$result = [ordered]@{
    status = $status
    schemaVersion = 2
    date = '2026-07-18'
    scope = 'M9 bounded K-local performance expansion including preview, batch, and memory samples; no runtime or release-performance claim'
    iterations = [ordered]@{ capabilities = 5; assetIndexPerEdition = 3; workspacePreflightPerEdition = 3; faceGenProviderResolutionPerEdition = 3; faceGeomProviderBoundPerEdition = 3; previewRender = 3; faceGenBatch = 3; packageVerification = 3 }
    budgetsMs = $budgets
    measurementsMs = [ordered]@{ capabilities = $capabilities; assetIndex = $assetIndex; workspacePreflight = $preflight; faceGenProviderResolution = $providerResolution; faceGeomProviderBound = $geomBound; previewRender = $previewRender; faceGenBatch = $faceGenBatch; faceGenBatchPeakWorkingSetBytes = $faceGenBatchPeakWorkingSetBytes; packageVerification = $packageVerification }
    summaryMs = $summary
    fixtureRoot = $fixtureRoot
    workRoot = $work
    packageRoot = $packageRoot
    releaseClaim = $false
}
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output (ConvertTo-Json @{ result = $status; output = $output; cliStartupColdMs = $summary.cliStartupCold; previewRenderColdMs = $summary.previewRenderCold; faceGenBatchColdMs = $summary.faceGenBatchCold; faceGenBatchPeakWorkingSetBytesMax = $summary.faceGenBatchPeakWorkingSetBytesMax; packageVerificationColdMs = $summary.packageVerificationCold } -Compress)
if ($status -ne 'PASS') { exit 1 }
