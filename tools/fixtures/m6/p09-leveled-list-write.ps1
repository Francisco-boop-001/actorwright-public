$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-leveled-list-write-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$fo4Source = Join-Path $root 'SourceFO4.esp'
$sseSource = Join-Path $root 'SourceSSE.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --leveled-dual $fo4Source $sseSource | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Leveled-list dual fixture generation failed' }

$fo4Proposal = Join-Path $root 'fo4.leveled-list-proposal.json'
$fo4ProposalResponse = Join-Path $root 'fo4-proposal-response.json'
$fo4Entries = '[{\"item\":\"SourceFO4.esp|0x900\",\"level\":2,\"count\":1,\"chanceNone\":10}]'
& $dotnet $cli leveled-list propose --edition fallout4 --plugin $fo4Source --list 0x801 --entries $fo4Entries --editor-id FixtureLeveledListFO4 --chance-none 25 --max-count 3 --calc-all-levels true --use-all true --output $fo4Proposal --json |
    Out-File -LiteralPath $fo4ProposalResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 leveled-list proposal failed' }
$fo4Output = Join-Path $root 'MaterializedFO4.esp'
$fo4WriteResponse = Join-Path $root 'fo4-write-response.json'
& $dotnet $cli leveled-list write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json |
    Out-File -LiteralPath $fo4WriteResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 leveled-list binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-leveled fallout4 $fo4Output 0x801 FixtureLeveledListFO4 0x900 2 1 10 25 3 true false true | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FO4 independent leveled-list verification failed' }

$sseProposal = Join-Path $root 'sse.leveled-list-proposal.json'
$sseProposalResponse = Join-Path $root 'sse-proposal-response.json'
$sseEntries = '[{\"item\":\"SourceSSE.esp|0x900\",\"level\":4,\"count\":2,\"chanceNone\":0}]'
& $dotnet $cli leveled-list propose --edition skyrimse --plugin $sseSource --list 0x801 --entries $sseEntries --editor-id FixtureLeveledListSSE --chance-none 15 --calc-each-in-count true --use-all true --output $sseProposal --json |
    Out-File -LiteralPath $sseProposalResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE leveled-list proposal failed' }
$sseOutput = Join-Path $root 'MaterializedSSE.esp'
$sseWriteResponse = Join-Path $root 'sse-write-response.json'
& $dotnet $cli leveled-list write --edition skyrimse --proposal $sseProposal --output $sseOutput --json |
    Out-File -LiteralPath $sseWriteResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE leveled-list binary write failed' }
& $dotnet run --project $generator --no-restore -- --verify-leveled skyrimse $sseOutput 0x801 FixtureLeveledListSSE 0x900 4 2 0 15 0 false true true | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'SSE independent leveled-list verification failed' }

$sseMaxProposal = Join-Path $root 'sse-max-count.leveled-list-proposal.json'
$sseMaxTampered = Get-Content -LiteralPath $sseProposal -Raw | ConvertFrom-Json
$sseMaxTampered.maxCount = 1
$sseMaxTampered | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $sseMaxProposal -Encoding utf8
$sseMaxOutput = Join-Path $root 'SseMaxCount.esp'
& $dotnet $cli leveled-list write --edition skyrimse --proposal $sseMaxProposal --output $sseMaxOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseMaxOutput)) { throw 'Unsupported SSE MaxCount was accepted' }

$sseEntryChanceProposal = Join-Path $root 'sse-entry-chance.leveled-list-proposal.json'
$sseEntryChanceTampered = Get-Content -LiteralPath $sseProposal -Raw | ConvertFrom-Json
$sseEntryChanceTampered.entries[0].chanceNone = 1
$sseEntryChanceTampered | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $sseEntryChanceProposal -Encoding utf8
$sseEntryChanceOutput = Join-Path $root 'SseEntryChance.esp'
& $dotnet $cli leveled-list write --edition skyrimse --proposal $sseEntryChanceProposal --output $sseEntryChanceOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $sseEntryChanceOutput)) { throw 'Unsupported SSE entry chance was accepted' }

$existingResponse = Join-Path $root 'existing-output-response.json'
& $dotnet $cli leveled-list write --edition fallout4 --proposal $fo4Proposal --output $fo4Output --json |
    Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Existing LVLI output was overwritten or accepted' }

$unknownProposal = Join-Path $root 'unknown-master.leveled-list-proposal.json'
$tampered = Get-Content -LiteralPath $fo4Proposal -Raw | ConvertFrom-Json
$tampered.entries[0].item = 'Unknown.esp|0x00000900'
$tampered | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $unknownProposal -Encoding utf8
$unknownOutput = Join-Path $root 'UnknownMaster.esp'
& $dotnet $cli leveled-list write --edition fallout4 --proposal $unknownProposal --output $unknownOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknownOutput)) { throw 'Unknown LVLI master was accepted' }

Write-Output "LEVELED LIST BINARY WRITE FIXTURES PASS $root"
