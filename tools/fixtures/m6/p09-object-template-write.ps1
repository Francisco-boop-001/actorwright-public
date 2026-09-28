$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-object-template-write-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_object_template_binary.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Object-template binary fixture generation failed' }
$combinations = Join-Path $root 'combinations.json'
@'
{
  "mode": "new",
  "targetFormId": "0x802",
  "editorId": "FixtureTemplateBinaryFO4",
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
$properties = Join-Path $root 'properties.json'
'[{"valueType":"FloatType","functionType":1,"propertyIndex":7,"value1Float":1.5,"value2Float":2.5,"stepValue":0.25,"combinationIndex":0},{"valueType":"FormIDInt","functionType":0,"propertyIndex":8,"value1FormId":"Source.esp|0x902","stepValue":1.0,"combinationIndex":0},{"valueType":"IntType","functionType":2,"propertyIndex":9,"value1Integer":16909060,"value2Integer":-7,"stepValue":0.0,"combinationIndex":0}]' | Set-Content -LiteralPath $properties -Encoding utf8
$proposal = Join-Path $root 'new.object-template-proposal.json'
$response = Join-Path $root 'new-response.json'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --combinations "@$combinations" --includes "@$includes" --output $proposal --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'New object-template proposal failed' }
$propertyProposal = Join-Path $root 'new.object-template-properties-proposal.json'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --properties "@$properties" --output $propertyProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Bound object-template property proposal failed' }
$output = Join-Path $root 'MaterializedFO4.esp'
& $dotnet $cli object-template write --edition fallout4 --proposal $proposal --properties $propertyProposal --output $output --json | Out-File -LiteralPath (Join-Path $root 'new-write-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'New object-template binary write failed' }
& python $verifier --output $output --proposal $proposal --properties $propertyProposal
if ($LASTEXITCODE -ne 0) { throw 'Independent new object-template binary verification failed' }

$tamperedProperties = Join-Path $root 'tampered.object-template-properties-proposal.json'
((Get-Content -LiteralPath $propertyProposal -Raw) -replace '(?<="inputSha256": ")[0-9A-Fa-f]+', ('0' * 64)) |
    Set-Content -LiteralPath $tamperedProperties -Encoding utf8
$tamperedOutput = Join-Path $root 'TamperedProperties.esp'
$ErrorActionPreference = 'Continue'
& $dotnet $cli object-template write --edition fallout4 --proposal $proposal --properties $tamperedProperties --output $tamperedOutput --json 2>$null | Out-Null
$tamperedExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($tamperedExit -ne 4 -or (Test-Path -LiteralPath $tamperedOutput)) { throw 'Mismatched property source hash was accepted' }

$overrideCombinations = Join-Path $root 'override-combinations.json'
'{"mode":"override","items":[{"displayName":"Override Variant","isEditorOnly":true,"keywords":[]}]}' | Set-Content -LiteralPath $overrideCombinations -Encoding utf8
$overrideProposal = Join-Path $root 'override.object-template-proposal.json'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --combinations "@$overrideCombinations" --includes '[]' --output $overrideProposal --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Override object-template proposal failed' }
$overrideOutput = Join-Path $root 'OverrideFO4.esp'
& $dotnet $cli object-template write --edition fallout4 --proposal $overrideProposal --output $overrideOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Override object-template binary write failed' }
& python $verifier --output $overrideOutput --proposal $overrideProposal
if ($LASTEXITCODE -ne 0) { throw 'Independent override object-template binary verification failed' }

$invalidCombinations = Join-Path $root 'invalid-combinations.json'
'{"mode":"new","targetFormId":"0x1000000","editorId":"InvalidTarget","items":[{}]}' | Set-Content -LiteralPath $invalidCombinations -Encoding utf8
$invalidProposal = Join-Path $root 'invalid.object-template-proposal.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --combinations "@$invalidCombinations" --includes '[]' --output $invalidProposal --json 2>$null | Out-Null
$invalidExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($invalidExit -ne 4 -or (Test-Path -LiteralPath $invalidProposal)) { throw 'Invalid object-template target was accepted' }

$sseProposal = Join-Path $root 'sse-invalid.object-template-proposal.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli object-template propose --edition skyrimse --plugin $sourceSse --source 0x801 --combinations "@$combinations" --includes "@$includes" --output $sseProposal --json 2>$null | Out-Null
$sseExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($sseExit -ne 4 -or (Test-Path -LiteralPath $sseProposal)) { throw 'Skyrim object-template binary proposal bypassed Fallout 4 gate' }

$ErrorActionPreference = 'Continue'
& $dotnet $cli object-template write --edition fallout4 --proposal $proposal --output $output --json 2>$null | Out-Null
$existingExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($existingExit -ne 4) { throw 'Existing object-template binary output was overwritten' }

Write-Output "OBJECT TEMPLATE BINARY WRITE FIXTURES PASS $root"
