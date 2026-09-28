$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m3-change-tracking-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_change_tracking.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$session = Join-Path $root 'fixture.changes-session.json'
$sessionJson = '{"baseline":[{"formId":"0x800","signature":"NPC_","editorId":"Alpha","fields":{"weight":50,"flag":true}},{"formId":"0x801","signature":"NPC_","editorId":"Same","fields":{"weight":10}}],"working":[{"formId":"0x800","signature":"NPC_","editorId":"Alpha","fields":{"weight":55,"flag":true}},{"formId":"0x801","signature":"NPC_","editorId":"Same","fields":{"weight":10}},{"formId":"0x802","signature":"ARMO","editorId":"New","fields":{"value":100}}]}'
Set-Content -LiteralPath $session -Value $sessionJson -Encoding utf8
$response = Join-Path $root 'response.json'
& $dotnet $cli changes list --edition fallout4 --session $session --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Change-tracking listing failed' }
& python $verifier --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent change-tracking verification failed' }

$duplicate = Join-Path $root 'duplicate.changes-session.json'
$duplicateJson = '{"baseline":[{"formId":"0x800","signature":"NPC_"},{"formId":"0x800","signature":"NPC_"}],"working":[]}'
Set-Content -LiteralPath $duplicate -Value $duplicateJson -Encoding utf8
& $dotnet $cli changes list --edition fallout4 --session $duplicate --json | Out-Null
if ($LASTEXITCODE -ne 4) { throw 'Duplicate change records were accepted' }

$nested = Join-Path $root 'nested.changes-session.json'
$nestedJson = '{"baseline":[{"formId":"0x800","signature":"NPC_","fields":{"nested":{"x":1}}}],"working":[]}'
Set-Content -LiteralPath $nested -Value $nestedJson -Encoding utf8
& $dotnet $cli changes list --edition fallout4 --session $nested --json | Out-Null
if ($LASTEXITCODE -ne 4) { throw 'Nested change fields were accepted' }

$sseResponse = Join-Path $root 'sse-response.json'
& $dotnet $cli changes list --game skyrimse --session $session --json | Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Skyrim SE change-tracking listing failed' }

Write-Output "CHANGE TRACKING FIXTURES PASS $root"
