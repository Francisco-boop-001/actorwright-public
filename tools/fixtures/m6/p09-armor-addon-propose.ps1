$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-addon-proposal-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_armor_addon_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --arma $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor-addon fixture generation failed' }
$patchNew = Join-Path $root 'new-patch.json'
@'
{
  "mode": "new",
  "editorId": "FixtureAddon",
  "slotMask": 16384,
  "race": "Source.esp|0x900",
  "footstepSet": "Source.esp|0x901",
  "malePriority": 10,
  "femalePriority": 20,
  "maleWeightSliderFlags": 2,
  "femaleWeightSliderFlags": 2,
  "detectionSound": 3,
  "weaponAdjust": 1.5,
  "maleModel": "meshes/armor/male.nif",
  "femaleModel": "meshes/armor/female.nif",
  "maleFirstPersonModel": "meshes/armor/male_fp.nif",
  "femaleFirstPersonModel": "meshes/armor/female_fp.nif",
  "maleModelFlags": 3,
  "femaleModelFlags": 4,
  "maleColorRemapIndex": 5,
  "femaleColorRemapIndex": 6,
  "maleSkinTexture": "Source.esp|0x902",
  "femaleSkinTexture": "Source.esp|0x903",
  "maleSkinTextureSwapList": "Source.esp|0x904",
  "femaleSkinTextureSwapList": "Source.esp|0x905",
  "maleMaterialSwap": "Source.esp|0x906",
  "femaleMaterialSwap": "Source.esp|0x907",
  "maleFirstPersonMaterialSwap": "Source.esp|0x908",
  "femaleFirstPersonMaterialSwap": "Source.esp|0x909",
  "artObject": "Source.esp|0x90A",
  "additionalRaces": ["Source.esp|0x90B"],
  "sculpt": [{"gender": 0, "bone": "Breast_skin", "x": 1.0, "y": 0.0, "z": -1.0}],
  "noUnderarmorScaling": true,
  "hasSculptData": true,
  "hiResFirstPersonOnly": false
}
'@ | Set-Content -LiteralPath $patchNew -Encoding utf8
$patchOverride = Join-Path $root 'override-patch.json'
@'
{
  "mode": "override",
  "maleModel": "meshes/armor/male.nif",
  "femaleModel": "meshes/armor/female.nif",
  "slotMask": 16384,
  "race": "Source.esp|0x900"
}
'@ | Set-Content -LiteralPath $patchOverride -Encoding utf8
$new = Join-Path $root 'new.armor-addon-proposal.json'
$override = Join-Path $root 'override.armor-addon-proposal.json'
$newResponse = Join-Path $root 'new-response.json'
$overrideResponse = Join-Path $root 'override-response.json'
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patchNew" --output $new --json |
    Out-File -LiteralPath $newResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'New armor-addon proposal failed' }
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patchOverride" --output $override --json |
    Out-File -LiteralPath $overrideResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Override armor-addon proposal failed' }
& python $verifier --source $source --new $new --override $override --new-response $newResponse --override-response $overrideResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent armor-addon verification failed' }

$unknown = Join-Path $root 'unknown.armor-addon-proposal.json'
$unknownPatch = Join-Path $root 'unknown-patch.json'
'{"mode":"new","editorId":"BadAddon","maleModel":"../escape.nif"}' | Set-Content -LiteralPath $unknownPatch -Encoding utf8
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --patch "@$unknownPatch" --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unsafe armor-addon model path was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.armor-addon-proposal.json'
$ssePatch = Join-Path $root 'sse-patch.json'
'{"mode":"new","editorId":"BadSseAddon","maleModelFlags":3}' | Set-Content -LiteralPath $ssePatch -Encoding utf8
& $dotnet $cli armor-addon propose --edition skyrimse --plugin $sourceSse --source 0x801 --patch "@$ssePatch" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim-only armor-addon gate was bypassed' }

$missing = Join-Path $root 'missing.armor-addon-proposal.json'
$missingPatch = Join-Path $root 'missing-patch.json'
'{"mode":"new","editorId":"BadMaster","race":"Missing.esp|0x900"}' | Set-Content -LiteralPath $missingPatch -Encoding utf8
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --patch "@$missingPatch" --output $missing --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $missing)) { throw 'Unknown armor-addon master was accepted' }

Write-Output "ARMOR ADDON PROPOSAL FIXTURES PASS $root"
