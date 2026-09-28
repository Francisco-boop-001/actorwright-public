$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m3-record-proposal-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_record_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$new = Join-Path $root 'new.record-proposal.json'
$newResponse = Join-Path $root 'new-response.json'
$masters = Join-Path $root 'masters.json'
Set-Content -LiteralPath $masters -Value '["Fallout4.esm"]' -Encoding utf8
& $dotnet $cli records propose --edition fallout4 --type NPC_ --mode new --form-id 0x1234 --editor-id npcm_Fixture --name 'Fixture NPC' --masters "@$masters" --output $new --json | Out-File -LiteralPath $newResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'New record proposal failed' }
$override = Join-Path $root 'override.record-proposal.json'
$overrideResponse = Join-Path $root 'override-response.json'
& $dotnet $cli records propose --game skyrimse --type NPC_ --mode override --form-id 0x2345 --source 0x2345 --editor-id npc_mOverride --output $override --json | Out-File -LiteralPath $overrideResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Override record proposal failed' }
& python $verifier --new $new --override $override --new-response $newResponse --override-response $overrideResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent record-proposal verification failed' }

$bad = Join-Path $root 'bad.record-proposal.json'
& $dotnet $cli records propose --edition fallout4 --type NPC_ --mode override --form-id 0x2345 --source 0x9999 --editor-id npc_mBad --output $bad --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $bad)) { throw 'Override source mismatch was accepted' }

$outside = 'F:\ExampleGame\record.record-proposal.json'
& $dotnet $cli records propose --edition fallout4 --type NPC_ --mode new --form-id 0x1235 --editor-id npc_mOutside --output $outside --json | Out-Null
if ($LASTEXITCODE -ne 3) { throw 'Outside-K record proposal was accepted' }

Write-Output "RECORD PROPOSAL FIXTURES PASS $root"
