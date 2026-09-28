$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_face_reset.py')).Path
$root = Join-Path $project '03-builds\work\m5-face-reset-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

$fo4Current = Join-Path $root 'fo4-current\Npc.face.json'
$fo4Baseline = Join-Path $root 'fo4-baseline\Npc.face.json'
$fo4Current | Split-Path | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
$fo4Baseline | Split-Path | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
$fo4CurrentJson = @'
{"schemaVersion":1,"game":"fallout4","npcFormId":"0x00000800","face-parts":{"headParts":["current-head"],"hairColor":12},"tints":{"layers":["current-tint"]},"vertex-morphs":{"Smile":0.25},"bone-regions":{"4":[1,2,3]},"unknown":{"keep":[1,2,3]}}
'@
$fo4BaselineJson = @'
{"schemaVersion":1,"game":"fallout4","npcFormId":"0x00000800","face-parts":{"headParts":["baseline-head"],"hairColor":20},"tints":{"layers":["baseline-tint"]},"vertex-morphs":{"Smile":0.75},"bone-regions":{"4":[9,8,7]},"unknown":{"keep":[1,2,3]}}
'@
$fo4CurrentJson | Out-File -LiteralPath $fo4Current -Encoding utf8
$fo4BaselineJson | Out-File -LiteralPath $fo4Baseline -Encoding utf8

foreach ($section in @('face-parts','tints','vertex-morphs','bone-regions')) {
    $output = Join-Path $root "fo4-output\$section\Npc.face.json"
    New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
    $evidence = Join-Path $root "fo4-output\$section\response.json"
    $hash = (Get-FileHash $fo4Current -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli face reset --game fallout4 --npc 0x800 --current $fo4Current --baseline $fo4Baseline --output $output --section $section --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "face reset failed for Fallout 4 section $section" }
    python $verifier --current $fo4Current --baseline $fo4Baseline --output $output --section $section
    if ($LASTEXITCODE -ne 0) { throw "independent face reset verification failed for Fallout 4 section $section" }
}

$sseCurrent = Join-Path $root 'sse-current\Npc.face.json'
$sseBaseline = Join-Path $root 'sse-baseline\Npc.face.json'
$sseCurrent | Split-Path | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
$sseBaseline | Split-Path | ForEach-Object { New-Item -ItemType Directory -Force $_ | Out-Null }
'{"schemaVersion":1,"game":"skyrimse","npcFormId":"0x00000801","skyrim-morphs":{"Smile":0.1},"skyrim-tints":{"layers":[1]},"unknown":{"keep":true}}' | Out-File -LiteralPath $sseCurrent -Encoding utf8
'{"schemaVersion":1,"game":"skyrimse","npcFormId":"0x00000801","skyrim-morphs":{"Smile":0.9},"skyrim-tints":{"layers":[2]},"unknown":{"keep":true}}' | Out-File -LiteralPath $sseBaseline -Encoding utf8
foreach ($section in @('skyrim-morphs','skyrim-tints')) {
    $output = Join-Path $root "sse-output\$section\Npc.face.json"
    New-Item -ItemType Directory -Force (Split-Path $output) | Out-Null
    $evidence = Join-Path $root "sse-output\$section\response.json"
    $hash = (Get-FileHash $sseCurrent -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli face reset --game skyrimse --npc 0x801 --current $sseCurrent --baseline $sseBaseline --output $output --section $section --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "face reset failed for Skyrim SE section $section" }
    python $verifier --current $sseCurrent --baseline $sseBaseline --output $output --section $section
    if ($LASTEXITCODE -ne 0) { throw "independent face reset verification failed for Skyrim SE section $section" }
}

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$badDuplicate = Join-Path $negative 'duplicate.face.json'
'{"schemaVersion":1,"game":"fallout4","npcFormId":"0x800","face-parts":{},"Face-Parts":{}}' | Out-File -LiteralPath $badDuplicate -Encoding utf8
$negativeOutput = Join-Path $negative 'out\Npc.face.json'; New-Item -ItemType Directory -Force (Split-Path $negativeOutput) | Out-Null
$hash = (Get-FileHash $badDuplicate -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli face reset --game fallout4 --npc 0x800 --current $badDuplicate --baseline $fo4Baseline --output $negativeOutput --section face-parts --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $negativeOutput)) { throw 'duplicate face-reset snapshot unexpectedly succeeded' }

$wrongSectionOutput = Join-Path $negative 'wrong-section\Npc.face.json'; New-Item -ItemType Directory -Force (Split-Path $wrongSectionOutput) | Out-Null
& $dotnet $cli face reset --game fallout4 --npc 0x800 --current $fo4Current --baseline $fo4Baseline --output $wrongSectionOutput --section skyrim-morphs --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $wrongSectionOutput)) { throw 'game-incompatible face-reset section unexpectedly succeeded' }

$staleOutput = Join-Path $negative 'stale\Npc.face.json'; New-Item -ItemType Directory -Force (Split-Path $staleOutput) | Out-Null
& $dotnet $cli face reset --game fallout4 --npc 0x800 --current $fo4Current --baseline $fo4Baseline --output $staleOutput --section tints --expected-sha256 ('0' * 64) --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $staleOutput)) { throw 'stale face-reset hash unexpectedly succeeded' }

python -m py_compile $verifier
Write-Output "P04 FACE RESET FIXTURES PASS $root"
