$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-outfit-list-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$generator = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_outfit_list.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Data') | Out-Null
$base = Join-Path $root 'Data\Base.esp'
$override = Join-Path $root 'Data\Override.esp'
$unusedSseBase = Join-Path $root 'Data\UnusedSseBase.esp'
$unusedSseOverride = Join-Path $root 'Data\UnusedSseOverride.esp'
$env:NUGET_PACKAGES = Join-Path $project '..\..\tools\external\nuget-packages-m2'
& $dotnet run --project $generator --no-restore -- --outfits $base $unusedSseBase | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Base outfit fixture generation failed' }
& $dotnet run --project $generator --no-restore -- --outfits $override $unusedSseOverride | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Outfit fixture generation failed' }
$data = Join-Path $root 'Data'
$response = Join-Path $root 'outfit-list-response.json'
& $dotnet $cli outfit list --edition fallout4 --data-root $data --plugins Base.esp,Override.esp --search M3Default --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Outfit list CLI evidence failed' }
& python $verifier --output $response
if ($LASTEXITCODE -ne 0) { throw 'Independent outfit list verification failed' }

$missing = Join-Path $root 'missing-response.json'
& $dotnet $cli outfit list --edition fallout4 --data-root $data --plugins Missing.esp --json |
    Out-File -LiteralPath $missing -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw "Expected missing plugin validation exit 4, got $LASTEXITCODE" }

$duplicate = Join-Path $root 'duplicate-response.json'
& $dotnet $cli outfit list --edition fallout4 --data-root $data --plugins Base.esp,Base.esp --json |
    Out-File -LiteralPath $duplicate -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw "Expected duplicate plugin validation exit 4, got $LASTEXITCODE" }

Write-Output "OUTFIT LIST FIXTURES PASS $root"
