$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-preset-resolve-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preset_resolve.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$map = Join-Path $root 'load-order.json'
$response = Join-Path $root 'resolved.json'
$missingResponse = Join-Path $root 'missing.json'
$invalidResponse = Join-Path $root 'invalid.json'
[IO.File]::WriteAllText($map, '{"ExampleHair.esp":2,"Fallout4.esm":0,"LooksMenu.esp":1,"Skyrim.esm":0}', (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli preset resolve --identifier 'ExampleHair.esp|01000010' --load-order $map --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'valid preset form identifier did not resolve' }
& $dotnet $cli preset resolve --identifier 'Missing.esp|01000010' --load-order $map --json | Out-File -LiteralPath $missingResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'missing plugin was accepted' }
& $dotnet $cli preset resolve --identifier 'not-an-identifier' --load-order $map --json | Out-File -LiteralPath $invalidResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'malformed preset identifier was accepted' }

& python $verifier --response $response --missing-response $missingResponse --invalid-response $invalidResponse
if ($LASTEXITCODE -ne 0) { throw 'independent preset resolution verification failed' }

$invalidMap = Join-Path $root 'invalid-map.json'
$invalidMapResponse = Join-Path $root 'invalid-map-response.json'
[IO.File]::WriteAllText($invalidMap, '{"ExampleHair.esp":256}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset resolve --identifier 'ExampleHair.esp|01000010' --load-order $invalidMap --json | Out-File -LiteralPath $invalidMapResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'invalid load-order index was accepted' }

$adsPath = "$map`:blocked"
& $dotnet $cli preset resolve --identifier 'ExampleHair.esp|01000010' --load-order $adsPath --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'alternate-data-stream load-order path was accepted' }

$outside = Join-Path $env:TEMP 'npcm-preset-resolve-outside.json'
if (Test-Path -LiteralPath $outside) { Remove-Item -LiteralPath $outside -Force }
& $dotnet $cli preset resolve --identifier 'ExampleHair.esp|01000010' --load-order 'F:\ExampleGame\Data\load-order.json' --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'protected-root load-order path was accepted' }

Write-Output "PRESET RESOLVE FIXTURES PASS $root"
