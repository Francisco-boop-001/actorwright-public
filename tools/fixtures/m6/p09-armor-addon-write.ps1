$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-addon-write-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$fo4Source = Join-Path $root 'SourceFO4.esp'
$sseSource = Join-Path $root 'SourceSSE.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --arma $fo4Source $sseSource | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor-addon dual fixture generation failed' }

$fo4Patch = Join-Path $root 'fo4-patch.json'
@'
{"mode":"new","targetFormId":"0x802","editorId":"FixtureArmorAddonFO4","slotMask":16384,"malePriority":2,"femalePriority":3,"maleWeightSliderFlags":1,"femaleWeightSliderFlags":0,"detectionSound":4,"weaponAdjust":1.5,"maleModel":"meshes/armor/addon_male.nif","femaleModel":"meshes/armor/addon_female.nif","maleFirstPersonModel":"meshes/armor/addon_male_fp.nif","femaleFirstPersonModel":"meshes/armor/addon_female_fp.nif"}
'@ | Set-Content -LiteralPath $fo4Patch -Encoding utf8
$fo4Proposal = Join-Path $root 'fo4.armor-addon-proposal.json'
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $fo4Source --source 0x801 --patch "@$fo4Patch" --output $fo4Proposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor-addon proposal failed' }
$fo4Output = Join-Path $root 'MaterializedFO4.esp'
& $dotnet $cli armor-addon write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor-addon binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-arma fallout4 $fo4Output 0x802 FixtureArmorAddonFO4 16384 4 1.5 meshes/armor/addon_male.nif meshes/armor/addon_female.nif meshes/armor/addon_male_fp.nif meshes/armor/addon_female_fp.nif 2 3 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 independent armor-addon verification failed' }

$fo4OverridePatch = Join-Path $root 'fo4-override-patch.json'
@'
{"mode":"override","maleModel":"meshes/armor/override_male.nif"}
'@ | Set-Content -LiteralPath $fo4OverridePatch -Encoding utf8
$fo4OverrideProposal = Join-Path $root 'fo4-override.armor-addon-proposal.json'
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $fo4Source --source 0x801 --patch "@$fo4OverridePatch" --output $fo4OverrideProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor-addon override proposal failed' }
$fo4OverrideOutput = Join-Path $root 'OverrideFO4.esp'
& $dotnet $cli armor-addon write --edition fallout4 --proposal $fo4OverrideProposal --output $fo4OverrideOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor-addon override binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-arma fallout4 $fo4OverrideOutput 0x801 M6SourceArmorAddonFO4 - - - meshes/armor/override_male.nif - - - - - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 armor-addon override verification failed' }

$ssePatch = Join-Path $root 'sse-patch.json'
@'
{"mode":"new","targetFormId":"0x802","editorId":"FixtureArmorAddonSSE","slotMask":16384,"malePriority":2,"femalePriority":3,"maleWeightSliderFlags":1,"femaleWeightSliderFlags":0,"detectionSound":4,"weaponAdjust":1.5,"maleModel":"meshes/armor/addon_male.nif","femaleModel":"meshes/armor/addon_female.nif","maleFirstPersonModel":"meshes/armor/addon_male_fp.nif","femaleFirstPersonModel":"meshes/armor/addon_female_fp.nif"}
'@ | Set-Content -LiteralPath $ssePatch -Encoding utf8
$sseProposal = Join-Path $root 'sse.armor-addon-proposal.json'
& $dotnet $cli armor-addon propose --edition skyrimse --plugin $sseSource --source 0x801 --patch "@$ssePatch" --output $sseProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Skyrim armor-addon proposal failed' }
$sseOutput = Join-Path $root 'MaterializedSSE.esp'
& $dotnet $cli armor-addon write --edition skyrimse --proposal $sseProposal --output $sseOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Skyrim armor-addon binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-arma skyrimse $sseOutput 0x802 FixtureArmorAddonSSE 16384 4 1.5 meshes/armor/addon_male.nif meshes/armor/addon_female.nif meshes/armor/addon_male_fp.nif meshes/armor/addon_female_fp.nif 2 3 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Skyrim independent armor-addon verification failed' }

$existingResponse = Join-Path $root 'existing-output-response.json'
& $dotnet $cli armor-addon write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Existing ARMA output was overwritten or accepted' }

Write-Output "ARMOR ADDON BINARY WRITE FIXTURES PASS $root"
