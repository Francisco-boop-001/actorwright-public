$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facegen-plugin-target-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_plugin_target.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)

$valid = Join-Path $project '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$zero = Join-Path $project '01-source-copies\m5-fixtures\zero-shapes.json'
$zeroCopy = Join-Path $root 'zero-802.json'
$zeroText = [IO.File]::ReadAllText($zero).Replace('0x00000800', '0x00000802')
[IO.File]::WriteAllText($zeroCopy, $zeroText, $utf8)
$missing = Join-Path $root 'missing.json'
$excluded = Join-Path $root 'excluded.json'
$batch = Join-Path $root 'target.json'
$batchObject = [ordered]@{
    schemaVersion = 1
    edition = 'fallout4'
    targetPlugin = 'Target.esp'
    entries = @(
        [ordered]@{ winningPlugin = 'Target.esp'; npcFormId = '0x800'; manifestPath = $valid }
        [ordered]@{ winningPlugin = 'Other.esp'; npcFormId = '0x801'; manifestPath = $excluded }
        [ordered]@{ winningPlugin = 'Target.esp'; npcFormId = '0x802'; manifestPath = $zeroCopy }
        [ordered]@{ winningPlugin = 'Target.esp'; npcFormId = '0x803'; manifestPath = $missing }
    )
}
[IO.File]::WriteAllText($batch, ($batchObject | ConvertTo-Json -Compress), $utf8)
$output = Join-Path $root 'target-a.json'
$repeat = Join-Path $root 'target-b.json'
$response = Join-Path $root 'target-response.json'
& $dotnet $cli facegen build-plugin --edition fallout4 --manifest $batch --plugin Target.esp --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'A selected plugin-target failure was hidden behind a successful exit' }
& $dotnet $cli facegen build-plugin --edition fallout4 --manifest $batch --plugin Target.esp --output $repeat --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'Repeated plugin-target failure unexpectedly returned success' }
& python $verifier --manifest $batch --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent plugin-target verification failed' }

$wrongPlugin = Join-Path $root 'wrong-plugin.json'
$wrongText = [IO.File]::ReadAllText($batch).Replace('"targetPlugin":"Target.esp"', '"targetPlugin":"Other.esp"')
[IO.File]::WriteAllText($wrongPlugin, $wrongText, $utf8)
$wrongOutput = Join-Path $root 'wrong-plugin-output.json'
& $dotnet $cli facegen build-plugin --edition fallout4 --manifest $wrongPlugin --plugin Target.esp --output $wrongOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $wrongOutput)) { throw 'Manifest/plugin target mismatch was accepted' }

$outsideOutput = Join-Path $root 'outside-output.json'
& $dotnet $cli facegen build-plugin --edition fallout4 --manifest 'F:\ExampleGame\Data\missing-target.json' --plugin Target.esp --output $outsideOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outsideOutput)) { throw 'Protected-root plugin-target manifest was accepted' }

Write-Output "FACEGEN PLUGIN TARGET FIXTURES PASS $root"
