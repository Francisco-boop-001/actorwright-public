$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-write-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$fo4Source = Join-Path $root 'SourceFO4.esp'
$sseSource = Join-Path $root 'SourceSSE.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $fo4Source $sseSource | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor dual fixture generation failed' }

$fo4Proposal = Join-Path $root 'fo4.armor-proposal.json'
$fo4Patch = Join-Path $root 'fo4-patch.json'
@'
{"mode":"new","targetFormId":"0x802","editorId":"FixtureArmorFO4","name":"Fixture Armor FO4","value":125,"weight":12.5,"health":80,"armorRating":35,"slotMask":16384}
'@ | Set-Content -LiteralPath $fo4Patch -Encoding utf8
& $dotnet $cli armor propose --edition fallout4 --plugin $fo4Source --source 0x801 --patch "@$fo4Patch" --output $fo4Proposal --json | Out-File -LiteralPath (Join-Path $root 'fo4-proposal-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor proposal failed' }
$fo4Output = Join-Path $root 'MaterializedFO4.esp'
& $dotnet $cli armor write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json | Out-File -LiteralPath (Join-Path $root 'fo4-write-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-armor fallout4 $fo4Output 0x802 FixtureArmorFO4 125 12.5 35 80 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 independent armor verification failed' }

$fo4OverridePatch = Join-Path $root 'fo4-override-patch.json'
@'
{"mode":"override","name":"Override Armor FO4","value":77,"weight":4.5,"health":22,"armorRating":11}
'@ | Set-Content -LiteralPath $fo4OverridePatch -Encoding utf8
$fo4OverrideProposal = Join-Path $root 'fo4-override.armor-proposal.json'
& $dotnet $cli armor propose --edition fallout4 --plugin $fo4Source --source 0x801 --patch "@$fo4OverridePatch" --output $fo4OverrideProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor override proposal failed' }
$fo4OverrideOutput = Join-Path $root 'OverrideFO4.esp'
& $dotnet $cli armor write --edition fallout4 --proposal $fo4OverrideProposal --output $fo4OverrideOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor override binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-armor fallout4 $fo4OverrideOutput 0x801 M6SourceArmorFO4 77 4.5 11 22 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor override verification failed' }

$sseProposal = Join-Path $root 'sse.armor-proposal.json'
$ssePatch = Join-Path $root 'sse-patch.json'
@'
{"mode":"new","targetFormId":"0x802","editorId":"FixtureArmorSSE","name":"Fixture Armor SSE","value":125,"weight":2.5,"armorRating":35.5,"slotMask":16384}
'@ | Set-Content -LiteralPath $ssePatch -Encoding utf8
& $dotnet $cli armor propose --edition skyrimse --plugin $sseSource --source 0x801 --patch "@$ssePatch" --output $sseProposal --json | Out-File -LiteralPath (Join-Path $root 'sse-proposal-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Skyrim armor proposal failed' }
$sseOutput = Join-Path $root 'MaterializedSSE.esp'
& $dotnet $cli armor write --edition skyrimse --proposal $sseProposal --output $sseOutput --json | Out-File -LiteralPath (Join-Path $root 'sse-write-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Skyrim armor binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-armor skyrimse $sseOutput 0x802 FixtureArmorSSE 125 2.5 35.5 0 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Skyrim independent armor verification failed' }

$existingResponse = Join-Path $root 'existing-output-response.json'
& $dotnet $cli armor write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Existing ARMO output was overwritten or accepted' }

$badTargetProposal = Join-Path $root 'bad-target.armor-proposal.json'
$tampered = Get-Content -LiteralPath $fo4Proposal -Raw | ConvertFrom-Json
$tampered.targetFormId = '0x01000000'
$tampered | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $badTargetProposal -Encoding utf8
$badTargetOutput = Join-Path $root 'BadTarget.esp'
& $dotnet $cli armor write --edition fallout4 --proposal $badTargetProposal --output $badTargetOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badTargetOutput)) { throw 'Out-of-range ARMO target was accepted' }

Write-Output "ARMOR BINARY WRITE FIXTURES PASS $root"
