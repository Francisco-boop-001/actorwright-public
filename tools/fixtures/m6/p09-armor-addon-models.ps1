$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-armor-addon-models-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_armor_addon_models.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Armor-addon model fixture generation failed' }
$entries = Join-Path $root 'models.json'
'[{"index":2,"addon":"Source.esp|0x900"},{"index":7,"addon":"Source.esp|0x901"}]' | Set-Content -LiteralPath $entries -Encoding utf8
$proposal = Join-Path $root 'models.armor-addon-model-proposal.json'
$response = Join-Path $root 'response.json'
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --models "@$entries" --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Armor-addon model proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent armor-addon model verification failed' }

$duplicate = Join-Path $root 'duplicate.armor-addon-model-proposal.json'
$duplicateEntries = Join-Path $root 'duplicate.json'
'[{"index":2,"addon":"Source.esp|0x900"},{"index":2,"addon":"Source.esp|0x901"}]' | Set-Content -LiteralPath $duplicateEntries -Encoding utf8
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --models "@$duplicateEntries" --output $duplicate --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $duplicate)) { throw 'Duplicate armor-addon model index was accepted' }

$unknown = Join-Path $root 'unknown.armor-addon-model-proposal.json'
$unknownEntries = Join-Path $root 'unknown.json'
'[{"index":2,"addon":"Missing.esp|0x900"}]' | Set-Content -LiteralPath $unknownEntries -Encoding utf8
& $dotnet $cli armor-addon propose --edition fallout4 --plugin $source --source 0x801 --models "@$unknownEntries" --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unknown armor-addon model master was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.armor-addon-model-proposal.json'
& $dotnet $cli armor-addon propose --edition skyrimse --plugin $sourceSse --source 0x801 --models "@$entries" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim nonzero armor-addon model index was accepted' }

Write-Output "ARMOR ADDON MODELS FIXTURES PASS $root"
