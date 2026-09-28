$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-presets-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_presets.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
$assets = @([ordered]@{ category = 'face'; path = 'meshes/face.nif'; provider = 'fixture'; sha256 = $hash })
$cameras = @([ordered]@{ id = 'portrait'; version = 3; yaw = 15; pitch = -4; distance = 5.5; fov = 42 })
$lights = @([ordered]@{ id = 'key'; azimuth = -30; elevation = 25; intensity = 1.2; red = 1; green = 0.9; blue = 0.8 })
$lighting = @([ordered]@{ id = 'studio'; version = 2; ambient = 0.35; lights = $lights })
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x901'; assets = $assets; cameraPresets = $cameras; lightingPresets = $lighting }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$output = Join-Path $root 'scene-a.json'
$repeat = Join-Path $root 'scene-b.json'
$response = Join-Path $root 'scene-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --camera portrait --lighting studio --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview preset evidence build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --camera portrait --lighting studio --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview preset evidence build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent preview preset verification failed' }

$unknownOutput = Join-Path $root 'unknown-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --camera missing --output $unknownOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $unknownOutput)) { throw 'Unknown camera preset was accepted' }

$duplicate = Join-Path $root 'duplicate.json'
$duplicateObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x901'; assets = $assets; cameraPresets = @($cameras[0], $cameras[0]) }
[IO.File]::WriteAllText($duplicate, ($duplicateObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$duplicateOutput = Join-Path $root 'duplicate-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $duplicate --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $duplicateOutput)) { throw 'Duplicate camera preset was accepted' }

Write-Output "PREVIEW PRESETS FIXTURES PASS $root"
