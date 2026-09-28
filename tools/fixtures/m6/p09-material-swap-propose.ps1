$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-material-swap-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_material_swap_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --mswp $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Material-swap fixture generation failed' }
$patch = Join-Path $root 'patch.json'
@'
{
  "mode": "new",
  "editorId": "FixtureSwap",
  "treeFolder": "Armor",
  "entries": [
    {"originalMaterial": "materials\\armor\\old.bgsm", "replacementMaterial": "materials/armor/new.bgsm", "colorRemapIndex": 0.25, "treeFolder": "Armor"},
    {"originalMaterial": "materials/armor/old2.bgem", "replacementMaterial": ""}
  ]
}
'@ | Set-Content -LiteralPath $patch -Encoding utf8
$proposal = Join-Path $root 'new.material-swap-proposal.json'
$response = Join-Path $root 'response.json'
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$patch" --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Material-swap proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent material-swap verification failed' }

$unsafe = Join-Path $root 'unsafe.material-swap-proposal.json'
$unsafePatch = Join-Path $root 'unsafe.json'
'{"mode":"new","editorId":"UnsafeSwap","entries":[{"originalMaterial":"../escape.bgsm","replacementMaterial":"materials/safe.bgsm"}]}' | Set-Content -LiteralPath $unsafePatch -Encoding utf8
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$unsafePatch" --output $unsafe --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unsafe)) { throw 'Unsafe material path was accepted' }

$empty = Join-Path $root 'empty.material-swap-proposal.json'
$emptyPatch = Join-Path $root 'empty.json'
'{"mode":"new","editorId":"EmptySwap","entries":[{"originalMaterial":"","replacementMaterial":""}]}' | Set-Content -LiteralPath $emptyPatch -Encoding utf8
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$emptyPatch" --output $empty --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $empty)) { throw 'Empty material substitution was accepted' }

$numericMode = Join-Path $root 'numeric-mode.material-swap-proposal.json'
$numericModePatch = Join-Path $root 'numeric-mode.json'
'{"mode":0,"editorId":"NumericMode","entries":[{"originalMaterial":"materials/safe.bgsm","replacementMaterial":""}]}' | Set-Content -LiteralPath $numericModePatch -Encoding utf8
& $dotnet $cli material-swap propose --edition fallout4 --plugin $source --source 0x801 --patch "@$numericModePatch" --output $numericMode --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $numericMode)) { throw 'Numeric material-swap mode was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.material-swap-proposal.json'
& $dotnet $cli material-swap propose --edition skyrimse --plugin $sourceSse --source 0x801 --patch "@$patch" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim material-swap request bypassed Fallout 4 gate' }

Write-Output "MATERIAL SWAP FIXTURES PASS $root"
