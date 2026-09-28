$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_skin_wname.py')).Path
$source = (Resolve-Path (Join-Path $project '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp')).Path
$sseSource = (Resolve-Path (Join-Path $project '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp')).Path
$root = Join-Path $project '03-builds\work\m5-skin-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()
$output = Join-Path $root 'M5SkinFO4.esp'
$response = Join-Path $root 'apply.json'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $output --npc 0x800 --skin 'M2FixtureFO4.esp|0x801' --expected-sha256 $hash --apply --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 WNAM skin patch failed' }
python $verifier --plugin $output --npc 0x800 --expected 'M2FixtureFO4.esp|0x801'
if ($LASTEXITCODE -ne 0) { throw 'independent WNAM verification failed' }

$presetOutput = Join-Path $root 'preset-refused.esp'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $presetOutput --npc 0x800 --preset-skin 'Vanilla CBBE' --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $presetOutput)) { throw 'unverified preset-skin was accepted' }

$sseOutput = Join-Path $root 'sse-refused.esp'
& $dotnet $cli body patch --game skyrimse --input-plugin $sseSource --output $sseOutput --npc 0x800 --clear-skin --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $sseOutput)) { throw 'Skyrim WNAM route was accepted' }

$externalOutput = Join-Path $root 'external-refused.esp'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $externalOutput --npc 0x800 --skin 'Other.esp|0x801' --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $externalOutput)) { throw 'external skin reference was accepted' }

$staleOutput = Join-Path $root 'stale-refused.esp'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $staleOutput --npc 0x800 --skin 'M2FixtureFO4.esp|0x801' --expected-sha256 ('0' * 64) --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $staleOutput)) { throw 'stale WNAM input hash was accepted' }

$existingResponse = Join-Path $root 'existing-refused.json'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $output --npc 0x800 --skin 'M2FixtureFO4.esp|0x801' --expected-sha256 $hash --apply --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'existing WNAM output was overwritten' }

python -m py_compile $verifier
Write-Output "P05 SKIN FIXTURES PASS $root"
