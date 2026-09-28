$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_body_morph_regions.py')).Path
$source = (Resolve-Path (Join-Path $project '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp')).Path
$sseSource = (Resolve-Path (Join-Path $project '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp')).Path
$root = Join-Path $project '03-builds\work\m5-body-morph-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()
$regions = '{"head":0.25,"upperTorso":-0.5,"arms":0.1,"lowerTorso":0,"legs":1}'
$expected = 'head=0.25,upperTorso=-0.5,arms=0.1,lowerTorso=0,legs=1'
$regionsFile = Join-Path $root 'regions.json'
[IO.File]::WriteAllText($regionsFile, $regions, (New-Object Text.UTF8Encoding($false)))
$output = Join-Path $root 'M5BodyMorphFO4.esp'
$response = Join-Path $root 'apply.json'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $output --npc 0x800 --regions "@$regionsFile" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 MRSV body-region patch failed' }
python $verifier --plugin $output --npc 0x800 --expected $expected
if ($LASTEXITCODE -ne 0) { throw 'independent MRSV verification failed' }

$verified = Join-Path $root 'verify.json'
& $dotnet $cli plugin verify --game fallout4 --source-plugin $source --output-plugin $output --form-id 0x800 --regions "@$regionsFile" --json | Out-File -LiteralPath $verified -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'MRSV plugin verification failed' }

$rangeOutput = Join-Path $root 'range-refused.esp'
$rangeFile = Join-Path $root 'range.json'; [IO.File]::WriteAllText($rangeFile, '{"head":2}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $rangeOutput --npc 0x800 --regions "@$rangeFile" --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $rangeOutput)) { throw 'out-of-range MRSV value was accepted' }

$unknownOutput = Join-Path $root 'unknown-refused.esp'
$unknownFile = Join-Path $root 'unknown.json'; [IO.File]::WriteAllText($unknownFile, '{"neck":0.2}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $unknownOutput --npc 0x800 --regions "@$unknownFile" --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $unknownOutput)) { throw 'unknown MRSV region was accepted' }

$duplicateOutput = Join-Path $root 'duplicate-refused.esp'
$duplicateFile = Join-Path $root 'duplicate.json'; [IO.File]::WriteAllText($duplicateFile, '{"head":0.1,"HEAD":0.2}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $duplicateOutput --npc 0x800 --regions "@$duplicateFile" --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $duplicateOutput)) { throw 'duplicate MRSV region was accepted' }

$sseOutput = Join-Path $root 'sse-refused.esp'
$sseFile = Join-Path $root 'sse.json'; [IO.File]::WriteAllText($sseFile, '{"head":0.2}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli body patch --game skyrimse --input-plugin $sseSource --output $sseOutput --npc 0x800 --regions "@$sseFile" --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $sseOutput)) { throw 'Skyrim MRSV route was accepted' }

$staleOutput = Join-Path $root 'stale-refused.esp'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $staleOutput --npc 0x800 --regions "@$regionsFile" --expected-sha256 ('0' * 64) --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $staleOutput)) { throw 'stale MRSV input hash was accepted' }

$existingResponse = Join-Path $root 'existing-refused.json'
& $dotnet $cli body patch --game fallout4 --input-plugin $source --output $output --npc 0x800 --regions "@$regionsFile" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'existing MRSV output was overwritten' }

python -m py_compile $verifier
Write-Output "P05 BODY MORPH FIXTURES PASS $root"
