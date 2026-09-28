$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-outfit-proposal-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_outfit_proposal.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'Source.esp'
$unused = Join-Path $root 'UnusedSse.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --outfits $source $unused | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Outfit proposal fixture generation failed' }
$create = Join-Path $root 'create.outfit-proposal.json'
$override = Join-Path $root 'override.outfit-proposal.json'
$sseCreate = Join-Path $root 'sse-create.outfit-proposal.json'
$createResponse = Join-Path $root 'create-response.json'
$overrideResponse = Join-Path $root 'override-response.json'
$sseCreateResponse = Join-Path $root 'sse-create-response.json'
$items = 'Source.esp|0x803'
& $dotnet $cli outfit propose --edition fallout4 --plugin $source --source 0x801 --mode new --target-form 0x804 --editor-id FixtureNewOutfit --items $items --output $create --json |
    Out-File -LiteralPath $createResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Create outfit proposal failed' }
& $dotnet $cli outfit propose --edition fallout4 --plugin $source --source 0x801 --mode override --items $items --output $override --json |
    Out-File -LiteralPath $overrideResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Override outfit proposal failed' }
$sseItems = 'UnusedSse.esp|0x803'
& $dotnet $cli outfit propose --edition skyrimse --plugin $unused --source 0x801 --mode new --target-form 0x804 --editor-id FixtureNewOutfitSse --items $sseItems --output $sseCreate --json |
    Out-File -LiteralPath $sseCreateResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE create outfit proposal failed' }
& python $verifier --source $source --create $create --override $override --create-response $createResponse --override-response $overrideResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent outfit proposal verification failed' }

$createdBinary = Join-Path $root 'created.esp'
$overriddenBinary = Join-Path $root 'overridden.esp'
$sseCreatedBinary = Join-Path $root 'sse-created.esp'
& $dotnet $cli outfit write --edition fallout4 --proposal $create --output $createdBinary --json | Out-File -LiteralPath (Join-Path $root 'create-write-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 outfit binary write failed' }
& $dotnet $cli outfit write --edition fallout4 --proposal $override --output $overriddenBinary --json | Out-File -LiteralPath (Join-Path $root 'override-write-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 override binary write failed' }
& $dotnet $cli outfit write --edition skyrimse --proposal $sseCreate --output $sseCreatedBinary --json | Out-File -LiteralPath $sseCreateResponse -Append -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE outfit binary write failed' }
$fixtureRun = @('--verify-outfit', 'fallout4', $createdBinary, '0x804', 'FixtureNewOutfit', '0x803')
& $dotnet run --project $generator --no-restore -- $fixtureRun
if ($LASTEXITCODE -ne 0) { throw 'FO4 outfit binary verification failed' }
$fixtureRun = @('--verify-outfit', 'fallout4', $overriddenBinary, '0x801', 'M3DefaultOutfitFO4', '0x803')
& $dotnet run --project $generator --no-restore -- $fixtureRun
if ($LASTEXITCODE -ne 0) { throw 'FO4 override binary verification failed' }
$fixtureRun = @('--verify-outfit', 'skyrimse', $sseCreatedBinary, '0x804', 'FixtureNewOutfitSse', '0x803')
& $dotnet run --project $generator --no-restore -- $fixtureRun
if ($LASTEXITCODE -ne 0) { throw 'SSE outfit binary verification failed' }

& $dotnet $cli outfit write --edition fallout4 --proposal $create --output $createdBinary --json | Out-Null
if ($LASTEXITCODE -ne 4) { throw 'Existing outfit binary output was overwritten' }
$unsupportedBinary = Join-Path $root 'unsupported.esl'
& $dotnet $cli outfit write --edition fallout4 --proposal $create --output $unsupportedBinary --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unsupportedBinary)) { throw 'Unsupported ESL output was accepted' }

$unknown = Join-Path $root 'unknown-master.outfit-proposal.json'
& $dotnet $cli outfit propose --edition fallout4 --plugin $source --source 0x801 --mode new --target-form 0x805 --editor-id BadOutfit --items 'Missing.esp|0x900' --output $unknown --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknown)) { throw 'Unknown master item was accepted' }

$binary = Join-Path $root 'invalid.json'
& $dotnet $cli outfit propose --edition fallout4 --plugin $source --source 0x801 --mode new --target-form 0x806 --editor-id BadOutfit --items $items --output $binary --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $binary)) { throw 'Invalid proposal extension was accepted' }

Write-Output "OUTFIT PROPOSAL FIXTURES PASS $root"
