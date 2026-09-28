$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-looksmenu-load-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_looksmenu_load.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$input = Join-Path $root 'extended.json'
$response = Join-Path $root 'response.json'
[IO.File]::WriteAllText($input, @'
{"Gender":1,"Morphs":{"Presets":{"00ABCDEF":0.25},"Regions":{"00000004":[1.0,-2.0,3.0]},"Intensity":0.75,"NoseWidth":0.1},"BodyMorphs":{"CBBE Breast":0.25},"Tints":{"00000002":{"Color":2,"ColorID":22,"Percent":20,"Type":3},"00000001":{"Color":1,"ColorID":11,"Percent":10,"Type":1}},"TintOrder":["00000001",2],"Weight":[0.2,0.5,0.3],"EngineExtra":{"preserve":"diagnostic-only"}}
'@, (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset inspect --format looksmenu --edition fallout4 --input $input --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'LooksMenu load fixture failed' }
& python $verifier --input $input --response $response
if ($LASTEXITCODE -ne 0) { throw 'independent LooksMenu load verification failed' }

$duplicate = Join-Path $root 'duplicate.json'
[IO.File]::WriteAllText($duplicate, '{"Gender":1,"gender":2}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset inspect --format looksmenu --edition fallout4 --input $duplicate --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'duplicate LooksMenu keys were accepted' }

$badNumber = Join-Path $root 'bad-number.json'
[IO.File]::WriteAllText($badNumber, '{"Gender":256}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset inspect --format looksmenu --edition fallout4 --input $badNumber --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'out-of-range LooksMenu numeric data was accepted' }

Write-Output "LOOKSMENU LOAD FIXTURES PASS $root"
