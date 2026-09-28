$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-object-template-properties-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_object_template_properties.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$sourceSse = Join-Path $root 'SourceSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --armor $source $sourceSse | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Object-template property fixture generation failed' }
$properties = Join-Path $root 'properties.json'
@'
[
  {"valueType":"FloatType","functionType":1,"propertyIndex":7,"value1Float":1.5,"value2Float":2.5,"stepValue":0.25},
  {"valueType":"FormIDInt","functionType":0,"propertyIndex":8,"value1FormId":"Source.esp|0x902","stepValue":1.0}
]
'@ | Set-Content -LiteralPath $properties -Encoding utf8
$proposal = Join-Path $root 'new.object-template-properties-proposal.json'
$response = Join-Path $root 'response.json'
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --properties "@$properties" --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Object-template property proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent object-template property verification failed' }

$unknown = Join-Path $root 'unknown.object-template-properties-proposal.json'
$unknownProperties = Join-Path $root 'unknown.json'
'[{"valueType":"UnknownType","propertyIndex":1}]' | Set-Content -LiteralPath $unknownProperties -Encoding utf8
& $dotnet $cli object-template propose --edition fallout4 --plugin $source --source 0x801 --properties "@$unknownProperties" --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unknown OMOD value type was accepted' }

$sseInvalid = Join-Path $root 'sse-invalid.object-template-properties-proposal.json'
& $dotnet $cli object-template propose --edition skyrimse --plugin $sourceSse --source 0x801 --properties "@$properties" --output $sseInvalid --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseInvalid)) { throw 'Skyrim object-template property request bypassed Fallout 4 gate' }

Write-Output "OBJECT TEMPLATE PROPERTIES FIXTURES PASS $root"
