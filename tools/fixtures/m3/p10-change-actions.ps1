$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m3-change-actions-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_change_actions.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$session = Join-Path $root 'fixture.changes-session.json'
$sessionJson = '{"baseline":[{"formId":"0x800","signature":"NPC_","editorId":"Override","fields":{"weight":50}}],"working":[{"formId":"0x800","signature":"NPC_","editorId":"Override","fields":{"weight":55}},{"formId":"0x801","signature":"ARMO","editorId":"New","fields":{"value":10}}]}'
Set-Content -LiteralPath $session -Value $sessionJson -Encoding utf8
$reset = Join-Path $root 'reset.changes-action.json'
$resetResponse = Join-Path $root 'reset-response.json'
& $dotnet $cli changes update --edition fallout4 --session $session --record 0x800 --signature NPC_ --action reset --output $reset --json | Out-File -LiteralPath $resetResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Change reset proposal failed' }
$delete = Join-Path $root 'delete.changes-action.json'
$deleteResponse = Join-Path $root 'delete-response.json'
& $dotnet $cli changes update --game fallout4 --session $session --record 0x801 --action delete --output $delete --json | Out-File -LiteralPath $deleteResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Change delete proposal failed' }
& python $verifier --reset $reset --delete $delete --reset-response $resetResponse --delete-response $deleteResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent change-action verification failed' }

$invalid = Join-Path $root 'invalid.changes-action.json'
& $dotnet $cli changes update --edition fallout4 --session $session --record 0x801 --signature ARMO --action reset --output $invalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $invalid)) { throw 'Reset of a new record was accepted' }

$duplicateOutput = Join-Path $root 'duplicate.changes-action.json'
Copy-Item -LiteralPath $reset -Destination $duplicateOutput
& $dotnet $cli changes update --edition fallout4 --session $session --record 0x800 --signature NPC_ --action reset --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -ne 4) { throw 'Existing change-action output was overwritten' }

Write-Output "CHANGE ACTION FIXTURES PASS $root"
