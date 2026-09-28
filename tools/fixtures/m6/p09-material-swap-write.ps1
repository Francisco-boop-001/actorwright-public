$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-material-swap-write-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'SourceFO4.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --mswp $source (Join-Path $root 'UnusedSSE.esp') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap source fixture generation failed' }

$patch = Join-Path $root 'fo4-patch.json'
@'
{"mode":"new","targetFormId":"0x802","editorId":"FixtureMaterialSwapFO4","treeFolder":"Armor/Fixture","entries":[{"originalMaterial":"materials/armor/old.bgsm","replacementMaterial":"materials/armor/new.bgsm","colorRemapIndex":0.25},{"originalMaterial":"materials/armor/old2.bgem","replacementMaterial":"materials/armor/new2.bgem"}]}
'@ | Set-Content -LiteralPath $patch -Encoding utf8
$proposal = Join-Path $root 'fo4.material-swap-proposal.json'
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patch" --output $proposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap proposal failed' }
$output = Join-Path $root 'MaterializedFO4.esp'
& $dotnet $cli material-swap write --edition fallout4 --proposal $proposal --output $output --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-mswp fallout4 $output 0x802 FixtureMaterialSwapFO4 Armor/Fixture materials/armor/old.bgsm materials/armor/new.bgsm 0.25 materials/armor/old2.bgem materials/armor/new2.bgem - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Independent material-swap verification failed' }

$overridePatch = Join-Path $root 'fo4-override-patch.json'
@'
{"mode":"override","entries":[{"originalMaterial":"materials/armor/override-old.bgsm","replacementMaterial":"materials/armor/override-new.bgsm","colorRemapIndex":0.5}]}
'@ | Set-Content -LiteralPath $overridePatch -Encoding utf8
$overrideProposal = Join-Path $root 'fo4-override.material-swap-proposal.json'
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$overridePatch" --output $overrideProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap override proposal failed' }
$overrideOutput = Join-Path $root 'OverrideFO4.esp'
& $dotnet $cli material-swap write --edition fallout4 --proposal $overrideProposal --output $overrideOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap override binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-mswp fallout4 $overrideOutput 0x801 M6SourceMaterialSwapFO4 - materials/armor/override-old.bgsm materials/armor/override-new.bgsm 0.5 - - - | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Independent material-swap override verification failed' }

$invalidTargetPatch = Join-Path $root 'invalid-target-patch.json'
@'
{"mode":"new","targetFormId":"0x000000","editorId":"InvalidTarget","entries":[{"originalMaterial":"materials/armor/old.bgsm","replacementMaterial":"materials/armor/new.bgsm"}]}
'@ | Set-Content -LiteralPath $invalidTargetPatch -Encoding utf8
$invalidTargetProposal = Join-Path $root 'invalid-target.material-swap-proposal.json'
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$invalidTargetPatch" --output $invalidTargetProposal --json 2>$null | Out-Null
$invalidTargetExitCode = $LASTEXITCODE
if (($invalidTargetExitCode -ne 2 -and $invalidTargetExitCode -ne 4) -or (Test-Path -LiteralPath $invalidTargetProposal)) { throw 'Invalid MSWP target was accepted' }

$entryFolderPatch = Join-Path $root 'entry-folder-patch.json'
@'
{"mode":"new","targetFormId":"0x803","editorId":"UnsupportedEntryFolder","entries":[{"originalMaterial":"materials/armor/old.bgsm","replacementMaterial":"materials/armor/new.bgsm","treeFolder":"Unsupported"}]}
'@ | Set-Content -LiteralPath $entryFolderPatch -Encoding utf8
$entryFolderProposal = Join-Path $root 'entry-folder.material-swap-proposal.json'
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$entryFolderPatch" --output $entryFolderProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Entry-folder MSWP proposal failed unexpectedly' }
$entryFolderOutput = Join-Path $root 'UnsupportedEntryFolder.esp'
& $dotnet $cli material-swap write --edition fallout4 --proposal $entryFolderProposal --output $entryFolderOutput --json 2>$null | Out-Null
$entryFolderExitCode = $LASTEXITCODE
if ($entryFolderExitCode -ne 4 -or (Test-Path -LiteralPath $entryFolderOutput)) { throw 'Unsupported MSWP entry TreeFolder was silently dropped' }

$unsupportedOutput = Join-Path $root 'UnsupportedSSE.esp'
& $dotnet $cli material-swap write --edition skyrimse --proposal $proposal --output $unsupportedOutput --json 2>$null | Out-Null
$unsupportedExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
if ($unsupportedExitCode -ne 4 -or (Test-Path -LiteralPath $unsupportedOutput)) { throw 'Unsupported Skyrim MSWP write was accepted' }

$existingResponse = Join-Path $root 'existing-output-response.json'
& $dotnet $cli material-swap write --edition fallout4 --proposal $proposal --output $output --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Existing MSWP output was overwritten or accepted' }

Write-Output "MATERIAL SWAP BINARY WRITE FIXTURES PASS $root"
