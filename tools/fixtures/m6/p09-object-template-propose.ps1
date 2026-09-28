$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-object-template-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_object_template_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Object-template fixture generation failed' }
$combinations = Join-Path $root 'combinations.json'
@'
{
  "mode": "new",
  "targetFormId": "0x802",
  "editorId": "FixtureTemplate",
  "items": [{
    "displayName": "Default",
    "isDefault": true,
    "levelMin": 1,
    "levelMax": 20,
    "minLevelForRanks": 5,
    "altLevelsPerTier": 2,
    "keywords": ["Source.esp|0x900"]
  }]
}
'@ | Set-Content -LiteralPath $combinations -Encoding utf8
$includes = Join-Path $root 'includes.json'
'[{"combinationIndex":0,"mod":"Source.esp|0x901","attachPointIndex":3,"optional":true,"dontUseAll":false}]' | Set-Content -LiteralPath $includes -Encoding utf8
$proposal = Join-Path $root 'new.object-template-proposal.json'
$response = Join-Path $root 'response.json'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --combinations "@$combinations" --includes "@$includes" --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Object-template proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent object-template verification failed' }

$badParent = Join-Path $root 'bad-parent.object-template-proposal.json'
$badCombinations = Join-Path $root 'bad-parent.json'
'{"mode":"new","targetFormId":"0x803","editorId":"BadTemplate","items":[{"parentCombinationIndex":2}]}' | Set-Content -LiteralPath $badCombinations -Encoding utf8
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --combinations "@$badCombinations" --includes '[]' --output $badParent --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badParent)) { throw 'Out-of-range parent combination was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.object-template-proposal.json'
& $dotnet $cli object-template propose --edition skyrimse --plugin $sourceSse --source 0x801 --combinations "@$combinations" --includes "@$includes" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim object-template request bypassed Fallout 4 gate' }

Write-Output "OBJECT TEMPLATE FIXTURES PASS $root"
