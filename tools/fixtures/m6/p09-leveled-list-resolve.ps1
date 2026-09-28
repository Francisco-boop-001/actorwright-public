$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-leveled-list-resolution-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_leveled_list_resolution.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --leveled $source | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list resolution fixture generation failed' }
$proposal = Join-Path $root 'list.leveled-list-proposal.json'
$proposalResponse = Join-Path $root 'proposal-response.json'
$entries = '[{\"item\":\"Source.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":0},{\"item\":\"Source.esp|0x901\",\"level\":5,\"count\":2,\"chanceNone\":0}]'
& $dotnet $cli leveled-list propose --edition fallout4 --plugin $source --list 0x801 --entries $entries --editor-id FixtureLeveledList --calc-each-in-count true --use-all true --output $proposal --json |
    Out-File -LiteralPath $proposalResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list proposal for resolution fixture failed' }
$resolution = Join-Path $root 'resolved.leveled-list-resolution.json'
$response = Join-Path $root 'resolution-response.json'
& $dotnet $cli leveled-list resolve --edition fallout4 --list $proposal --seed 42 --output $resolution --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list resolution failed' }
& python $verifier --proposal $proposal --resolution $resolution --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent leveled-list resolution verification failed' }

$badSeed = Join-Path $root 'bad-seed.leveled-list-resolution.json'
& $dotnet $cli leveled-list resolve --edition fallout4 --list $proposal --seed not-a-seed --output $badSeed --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badSeed)) { throw 'Malformed resolution seed was accepted' }

$wrongExtension = Join-Path $root 'wrong.json'
& $dotnet $cli leveled-list resolve --edition fallout4 --list $proposal --seed 42 --output $wrongExtension --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $wrongExtension)) { throw 'Invalid resolution extension was accepted' }

Write-Output "LEVELED LIST RESOLUTION FIXTURES PASS $root"
