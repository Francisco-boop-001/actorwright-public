$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-damage-resistance-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_armor_damage_resistance.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor damage-resistance fixture generation failed' }
$entries = Join-Path $root 'resistances.json'
'[{"damageType":"Source.esp|0x900","value":10},{"damageType":"Source.esp|0x901","value":25}]' |
    Set-Content -LiteralPath $entries -Encoding utf8
$proposal = Join-Path $root 'armor.armor-damage-resist-proposal.json'
$response = Join-Path $root 'response.json'
& $dotnet $cli armor damage-resist --edition fallout4 --plugin $source --source 0x801 --damage-resist "@$entries" --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Armor damage-resistance proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent armor damage-resistance verification failed' }

$duplicate = Join-Path $root 'duplicate.armor-damage-resist-proposal.json'
$duplicateEntries = Join-Path $root 'duplicate.json'
'[{"damageType":"Source.esp|0x900","value":10},{"damageType":"Source.esp|0x900","value":11}]' |
    Set-Content -LiteralPath $duplicateEntries -Encoding utf8
& $dotnet $cli armor damage-resist --edition fallout4 --plugin $source --source 0x801 --damage-resist "@$duplicateEntries" --output $duplicate --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $duplicate)) { throw 'Duplicate damage type was accepted' }

$unknownProperty = Join-Path $root 'unknown-property.armor-damage-resist-proposal.json'
$unknownPropertyEntries = Join-Path $root 'unknown-property.json'
'[{"damageType":"Source.esp|0x900","value":10,"extra":true}]' |
    Set-Content -LiteralPath $unknownPropertyEntries -Encoding utf8
& $dotnet $cli armor damage-resist --edition fallout4 --plugin $source --source 0x801 --damage-resist "@$unknownPropertyEntries" --output $unknownProperty --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $unknownProperty)) { throw 'Unknown damage-resistance property was accepted' }

$unknown = Join-Path $root 'unknown.armor-damage-resist-proposal.json'
$unknownEntries = Join-Path $root 'unknown.json'
'[{"damageType":"Missing.esp|0x900","value":10}]' | Set-Content -LiteralPath $unknownEntries -Encoding utf8
& $dotnet $cli armor damage-resist --edition fallout4 --plugin $source --source 0x801 --damage-resist "@$unknownEntries" --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unknown damage type master was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.armor-damage-resist-proposal.json'
& $dotnet $cli armor damage-resist --edition skyrimse --plugin $sourceSse --source 0x801 --damage-resist "@$entries" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim-only armor damage-resistance gate was bypassed' }

Write-Output "ARMOR DAMAGE RESISTANCE FIXTURES PASS $root"
