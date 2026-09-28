$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-body-reset-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_body_reset.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Write-Json([string]$path, [string]$text) {
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding($false)))
}

$fo4Current = Join-Path $root 'fo4-current\Npc.body.json'
$fo4Baseline = Join-Path $root 'fo4-baseline\Npc.body.json'
$fo4Output = Join-Path $root 'fo4-output\Npc.body.json'
Write-Json $fo4Current @'
{"schemaVersion":1,"game":"fallout4","npcFormId":"0x800","weight":{"thin":0.2},"morphs":{"head":0.1},"sliders":{"Body":0.2},"skin":{"formId":"0x0100"},"overlays":[{"texture":"current.dds"}],"unknown":{"keep":true}}
'@
Write-Json $fo4Baseline @'
{"schemaVersion":1,"game":"fallout4","npcFormId":"0x800","weight":{"thin":0.8},"morphs":{"head":0.9},"sliders":{"Body":0.8},"skin":{"formId":"0x0200"},"overlays":[{"texture":"baseline.dds"}],"unknown":{"keep":true}}
'@
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fo4Output) | Out-Null
$hash = (Get-FileHash -LiteralPath $fo4Current -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli body reset --game fallout4 --npc 0x800 --current $fo4Current --baseline $fo4Baseline --output $fo4Output --section overlays --expected-sha256 $hash --apply --json | Out-File -LiteralPath (Join-Path $root 'fo4-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 body reset failed' }
& python $verifier --current $fo4Current --baseline $fo4Baseline --output $fo4Output --section overlays
if ($LASTEXITCODE -ne 0) { throw 'independent FO4 body reset verification failed' }

$sseCurrent = Join-Path $root 'sse-current\Npc.body.json'
$sseBaseline = Join-Path $root 'sse-baseline\Npc.body.json'
$sseOutput = Join-Path $root 'sse-output\Npc.body.json'
Write-Json $sseCurrent '{"schemaVersion":1,"game":"skyrimse","npcFormId":"0x801","weight":{"value":40},"sliders":{"Body":0.2},"overlays":[{"node":"Body [Ovl1]"}],"transforms":[{"node":"NPC Spine","scale":1.1}],"skin-overrides":[{"slotMask":32}],"unknown":{"keep":true}}'
Write-Json $sseBaseline '{"schemaVersion":1,"game":"skyrimse","npcFormId":"0x801","weight":{"value":70},"sliders":{"Body":0.8},"overlays":[{"node":"Body [Ovl2]"}],"transforms":[{"node":"NPC Spine","scale":0.9}],"skin-overrides":[{"slotMask":64}],"unknown":{"keep":true}}'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $sseOutput) | Out-Null
$hash = (Get-FileHash -LiteralPath $sseCurrent -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli body reset --game skyrimse --npc 0x801 --current $sseCurrent --baseline $sseBaseline --output $sseOutput --section transforms --expected-sha256 $hash --apply --json | Out-File -LiteralPath (Join-Path $root 'sse-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE body reset failed' }
& python $verifier --current $sseCurrent --baseline $sseBaseline --output $sseOutput --section transforms
if ($LASTEXITCODE -ne 0) { throw 'independent SSE body reset verification failed' }

$malformed = Join-Path $root 'malformed\Npc.body.json'
Write-Json $malformed '{"schemaVersion":1,"game":"fallout4","npcFormId":"0x800","weight":42}'
$malformedOutput = Join-Path $root 'malformed-output\Npc.body.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $malformedOutput) | Out-Null
& $dotnet $cli body reset --game fallout4 --npc 0x800 --current $malformed --baseline $fo4Baseline --output $malformedOutput --section weight --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $malformedOutput)) { throw 'malformed body snapshot was accepted' }

$wrongSectionOutput = Join-Path $root 'wrong-section\Npc.body.json'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $wrongSectionOutput) | Out-Null
& $dotnet $cli body reset --game fallout4 --npc 0x800 --current $fo4Current --baseline $fo4Baseline --output $wrongSectionOutput --section transforms --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $wrongSectionOutput)) { throw 'cross-game body reset section was accepted' }

Write-Output "BODY RESET FIXTURES PASS $root"
