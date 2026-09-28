$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facegen-corrections-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_corrections.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$fo4Fixture = Join-Path $project '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$fo4Manifest = Join-Path $root 'fo4-corrections.json'
$fo4Text = [IO.File]::ReadAllText($fo4Fixture).Trim()
$fo4Text = $fo4Text.Substring(0, $fo4Text.Length - 1) + ',"corrections":[{"kind":"ghoul-head-rear","trigger":true,"before":"default","after":"ghoul-fixed"},{"kind":"eyebrows-fixed-color","trigger":false,"before":"palette","after":"fixed"}]}'
[IO.File]::WriteAllText($fo4Manifest, $fo4Text, (New-Object Text.UTF8Encoding($false)))
$fo4Output = Join-Path $root 'fo4-a.json'
$fo4Repeat = Join-Path $root 'fo4-b.json'
$fo4Response = Join-Path $root 'fo4-response.json'
& $dotnet $cli facegen build --edition fallout4 --manifest $fo4Manifest --npc 0x00000800 --corrections auto --output $fo4Output --json | Out-File -LiteralPath $fo4Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 correction build failed' }
& $dotnet $cli facegen build --edition fallout4 --manifest $fo4Manifest --npc 0x00000800 --corrections auto --output $fo4Repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated FO4 correction build failed' }
& python $verifier --manifest $fo4Manifest --output $fo4Output --repeat $fo4Repeat --response $fo4Response
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4 correction verification failed' }

$sseFixture = Join-Path $project '01-source-copies\m5-fixtures\sse-facegen-valid.json'
$sseManifest = Join-Path $root 'sse-corrections.json'
$sseText = [IO.File]::ReadAllText($sseFixture).Trim()
$sseText = $sseText.Substring(0, $sseText.Length - 1) + ',"corrections":[{"kind":"sse-neutral-detail","trigger":true,"before":"detail.dds","after":"neutral.dds"}]}'
[IO.File]::WriteAllText($sseManifest, $sseText, (New-Object Text.UTF8Encoding($false)))
$sseOutput = Join-Path $root 'sse-a.json'
$sseRepeat = Join-Path $root 'sse-b.json'
$sseResponse = Join-Path $root 'sse-response.json'
& $dotnet $cli facegen build --edition skyrimse --manifest $sseManifest --corrections auto --output $sseOutput --json | Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE correction build failed' }
& $dotnet $cli facegen build --edition skyrimse --manifest $sseManifest --corrections auto --output $sseRepeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated SSE correction build failed' }
& python $verifier --manifest $sseManifest --output $sseOutput --repeat $sseRepeat --response $sseResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE correction verification failed' }

$wrongGame = Join-Path $root 'wrong-game.json'
$wrongText = $fo4Text.Replace('"ghoul-head-rear"', '"sse-neutral-detail"')
[IO.File]::WriteAllText($wrongGame, $wrongText, (New-Object Text.UTF8Encoding($false)))
$wrongOutput = Join-Path $root 'wrong-game-output.json'
& $dotnet $cli facegen build --edition fallout4 --manifest $wrongGame --output $wrongOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $wrongOutput)) { throw 'Cross-game correction was accepted' }

$outsideOutput = Join-Path $root 'outside.json'
& $dotnet $cli facegen build --edition fallout4 --manifest 'F:\ExampleGame\Data\missing-corrections.json' --output $outsideOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outsideOutput)) { throw 'Protected-root correction manifest was accepted' }

Write-Output "FACEGEN CORRECTIONS FIXTURES PASS $root"
