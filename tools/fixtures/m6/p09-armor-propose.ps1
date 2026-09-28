$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-proposal-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_armor_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor proposal fixture generation failed' }
$patchNew = Join-Path $root 'new-patch.json'
@'
{
  "mode": "new",
  "editorId": "FixtureArmor",
  "name": "Fixture Armor",
  "slotMask": 16384,
  "race": "Source.esp|0x900",
  "maleWorldModel": "meshes/armor/male.nif",
  "femaleWorldModel": "meshes/armor/female.nif",
  "value": 125,
  "weight": 12.5,
  "health": 80,
  "armorRating": 35,
  "keywords": ["Source.esp|0x901"],
  "armorAddons": [{"index": 0, "addon": "Source.esp|0x902"}]
}
'@ | Set-Content -LiteralPath $patchNew -Encoding utf8
$patchOverride = Join-Path $root 'override-patch.json'
@'
{
  "mode": "override",
  "name": "Fixture Armor",
  "slotMask": 16384,
  "race": "Source.esp|0x900",
  "maleWorldModel": "meshes/armor/male.nif",
  "femaleWorldModel": "meshes/armor/female.nif",
  "value": 125,
  "weight": 12.5,
  "health": 80,
  "armorRating": 35,
  "keywords": ["Source.esp|0x901"],
  "armorAddons": [{"index": 0, "addon": "Source.esp|0x902"}]
}
'@ | Set-Content -LiteralPath $patchOverride -Encoding utf8
$new = Join-Path $root 'new.armor-proposal.json'
$override = Join-Path $root 'override.armor-proposal.json'
$newResponse = Join-Path $root 'new-response.json'
$overrideResponse = Join-Path $root 'override-response.json'
& $dotnet $cli armor propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patchNew" --output $new --json |
    Out-File -LiteralPath $newResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'New armor proposal failed' }
& $dotnet $cli armor propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patchOverride" --output $override --json |
    Out-File -LiteralPath $overrideResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Override armor proposal failed' }
& python $verifier --source $source --new $new --override $override --new-response $newResponse --override-response $overrideResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent armor proposal verification failed' }

$unknown = Join-Path $root 'unknown.armor-proposal.json'
$unknownPatch = Join-Path $root 'unknown-patch.json'
@'
{"mode":"new","editorId":"BadArmor","keywords":["Missing.esp|0x900"]}
'@ | Set-Content -LiteralPath $unknownPatch -Encoding utf8
& $dotnet $cli armor propose --edition fallout4 --plugin $source --source 0x801 --patch "@$unknownPatch" --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unknown armor master was accepted' }

$invalid = Join-Path $root 'invalid.armor-proposal.json'
$invalidPatch = Join-Path $root 'invalid-patch.json'
@'
{"mode":"new","editorId":"BadArmor","maleWorldModel":"../escape.nif"}
'@ | Set-Content -LiteralPath $invalidPatch -Encoding utf8
& $dotnet $cli armor propose --edition fallout4 --plugin $source --source 0x801 --patch "@$invalidPatch" --output $invalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $invalid)) { throw 'Unsafe armor model path was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.armor-proposal.json'
$ssePatch = Join-Path $root 'sse-patch.json'
@'
{"mode":"new","editorId":"BadSseArmor","health":80,"armorRating":35.5}
'@ | Set-Content -LiteralPath $ssePatch -Encoding utf8
& $dotnet $cli armor propose --edition skyrimse --plugin $sourceSse --source 0x801 --patch "@$ssePatch" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim-only armor game gate was bypassed' }

Write-Output "ARMOR PROPOSAL FIXTURES PASS $root"
