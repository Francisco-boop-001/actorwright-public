$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$dotnet = Join-Path $projectRoot '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$work = Join-Path $projectRoot '03-builds\work\m8-benchmark-2026-07-17'
$output = Join-Path $projectRoot '05-reports\m8-performance-benchmark-2026-07-17.json'
$preset = Join-Path $projectRoot '01-source-copies\m4-fixtures\fo4-looksmenu.json'
$sourcePlugin = Join-Path $projectRoot '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp'
if (-not (Test-Path -LiteralPath $cli)) { throw "CLI build was not found: $cli" }
if (Test-Path -LiteralPath $output) { throw "refusing to overwrite existing output: $output" }
if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Measure-Npcm([string[]]$arguments, [int]$iterations) {
    $measurements = @()
    for ($index = 0; $index -lt $iterations; $index++) {
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        & $dotnet $cli @arguments *> $null
        $timer.Stop()
        if ($LASTEXITCODE -ne 0) { throw "benchmark command failed with exit ${LASTEXITCODE}: $($arguments -join ' ')" }
        $measurements += [math]::Round($timer.Elapsed.TotalMilliseconds, 3)
    }
    return $measurements
}

$capabilities = Measure-Npcm @('capabilities', '--json') 5
$schemaMeasurements = @()
for ($index = 0; $index -lt 5; $index++) {
    $schemaOutput = Join-Path $work "schema-$index.json"
    $schemaMeasurements += Measure-Npcm @('schema', 'export', '--output', $schemaOutput, '--json') 1
}
$packageMeasurements = @()
for ($index = 0; $index -lt 3; $index++) {
    $packageRoot = Join-Path $work "package-$index"
    New-Item -ItemType Directory -Path $packageRoot | Out-Null
    $packageMeasurements += Measure-Npcm @('pipeline', 'preset-to-npc', '--format', 'looksmenu', '--edition', 'fallout4', '--preset', $preset,
        '--source-plugin', $sourcePlugin, '--plugin', "Benchmark$index.esp", '--npc', '0x00000800', '--mod-name', "Benchmark$index.esp",
        '--output-root', $packageRoot, '--editor-id', "BenchmarkNpc$index", '--json') 1
}

function Median([double[]]$values) {
    $sorted = @($values | Sort-Object)
    if (($sorted.Count % 2) -eq 1) { return $sorted[[int]($sorted.Count / 2)] }
    $upper = [int]($sorted.Count / 2)
    return [math]::Round(($sorted[$upper - 1] + $sorted[$upper]) / 2, 3)
}

$budgets = @{
    capabilitiesColdMs = 2000
    schemaExportColdMs = 2000
    presetToNpcColdMs = 10000
}
$results = [ordered]@{
    status = 'PASS'
    schemaVersion = 1
    iterations = [ordered]@{ capabilities = $capabilities.Count; schemaExport = $schemaMeasurements.Count; presetToNpc = $packageMeasurements.Count }
    budgetsMs = $budgets
    measurementsMs = [ordered]@{
        capabilities = $capabilities
        schemaExport = $schemaMeasurements
        presetToNpc = $packageMeasurements
    }
    summaryMs = [ordered]@{
        capabilitiesCold = $capabilities[0]
        capabilitiesWarmMedian = Median $capabilities[1..($capabilities.Count - 1)]
        schemaExportCold = $schemaMeasurements[0]
        schemaExportWarmMedian = Median $schemaMeasurements[1..($schemaMeasurements.Count - 1)]
        presetToNpcCold = $packageMeasurements[0]
        presetToNpcWarmMedian = Median $packageMeasurements[1..($packageMeasurements.Count - 1)]
    }
    workRoot = $work
    releaseClaim = $false
}
if ($results.summaryMs.capabilitiesCold -gt $budgets.capabilitiesColdMs -or
    $results.summaryMs.schemaExportCold -gt $budgets.schemaExportColdMs -or
    $results.summaryMs.presetToNpcCold -gt $budgets.presetToNpcColdMs) {
    $results.status = 'FAIL'
}
$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $output -Encoding UTF8
Write-Output (ConvertTo-Json @{ result = $results.status; output = $output; capabilitiesColdMs = $results.summaryMs.capabilitiesCold; presetToNpcColdMs = $results.summaryMs.presetToNpcCold } -Compress)
if ($results.status -ne 'PASS') { exit 1 }
