$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-leveled-list-proposal-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_leveled_list_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --leveled $source | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list proposal fixture generation failed' }
$proposal = Join-Path $root 'leveled-list.leveled-list-proposal.json'
$response = Join-Path $root 'response.json'
$entries = '[{\"item\":\"Source.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":10},{\"item\":\"Source.esp|0x901\",\"level\":5,\"count\":2,\"chanceNone\":0}]'
& $dotnet $cli leveled-list propose --edition fallout4 --plugin $source --list 0x801 --entries $entries --editor-id FixtureLeveledList --chance-none 25 --max-count 3 --calc-all-levels true --use-all true --output $proposal --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list proposal failed' }
& python $verifier --source $source --proposal $proposal --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent leveled-list proposal verification failed' }

$duplicate = Join-Path $root 'duplicate.leveled-list-proposal.json'
$duplicateEntries = '[{\"item\":\"Source.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":10},{\"item\":\"Source.esp|0x900\",\"level\":5,\"count\":2,\"chanceNone\":0}]'
& $dotnet $cli leveled-list propose --edition fallout4 --plugin $source --list 0x801 --entries $duplicateEntries --output $duplicate --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $duplicate)) { throw 'Duplicate leveled-list item was accepted' }

$invalidChance = Join-Path $root 'invalid-chance.leveled-list-proposal.json'
$invalidEntries = '[{\"item\":\"Source.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":101}]'
& $dotnet $cli leveled-list propose --edition fallout4 --plugin $source --list 0x801 --entries $invalidEntries --output $invalidChance --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $invalidChance)) { throw 'Invalid leveled-list chance was accepted' }

Write-Output "LEVELED LIST PROPOSAL FIXTURES PASS $root"
