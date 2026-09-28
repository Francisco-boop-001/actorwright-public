$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facegen-batch-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_batch.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$valid = Join-Path $project '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$skipped = Join-Path $project '01-source-copies\m5-fixtures\zero-shapes.json'
$missing = Join-Path $root 'missing.json'
$batch = Join-Path $root 'batch.json'
$batchObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; manifests = @($valid, $skipped, $missing) }
$json = $batchObject | ConvertTo-Json -Compress
[IO.File]::WriteAllText($batch, $json, (New-Object Text.UTF8Encoding($false)))
$output = Join-Path $root 'batch-a.json'
$repeat = Join-Path $root 'batch-b.json'
$response = Join-Path $root 'batch-response.json'

& $dotnet $cli facegen bake-all --edition fallout4 --manifests $batch --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'A failed batch item was hidden behind a successful process exit' }
& $dotnet $cli facegen bake-all --edition fallout4 --manifests $batch --output $repeat --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'Repeated failed batch unexpectedly returned success' }
& python $verifier --manifest $batch --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent FaceGen batch verification failed' }

$duplicate = Join-Path $root 'duplicate.json'
$duplicateObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; manifests = @($valid, "  $valid  ") }
$duplicateObject | ConvertTo-Json -Compress | Set-Content -LiteralPath $duplicate -Encoding utf8
$duplicateOutput = Join-Path $root 'duplicate-output.json'
& $dotnet $cli facegen bake-all --edition fallout4 --manifests $duplicate --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $duplicateOutput)) { throw 'Whitespace duplicate batch entry was accepted' }

$outsideOutput = Join-Path $root 'outside-output.json'
& $dotnet $cli facegen bake-all --edition fallout4 --manifests 'F:\ExampleGame\Data\missing-batch.json' --output $outsideOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outsideOutput)) { throw 'Protected-root batch manifest was accepted' }

Write-Output "FACEGEN BATCH FIXTURES PASS $root"
