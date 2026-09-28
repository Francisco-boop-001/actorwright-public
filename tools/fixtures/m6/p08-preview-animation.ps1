$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-animation-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_animation.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
$assets = @([ordered]@{ category = 'body'; path = 'meshes/body.nif'; provider = 'fixture'; sha256 = $hash })
$animations = @([ordered]@{ id = 'walk'; path = 'animations/walk.hkx'; skeleton = 'meshes/skeleton.nif'; frames = 10; fps = 30; additive = $false })
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x902'; assets = $assets; animations = $animations }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$output = Join-Path $root 'scene-a.json'
$repeat = Join-Path $root 'scene-b.json'
$response = Join-Path $root 'scene-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --animation walk --frame 2 --fps 24 --play --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview animation evidence build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --animation walk --frame 2 --fps 24 --play --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview animation evidence build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent preview animation verification failed' }

$missingPosition = Join-Path $root 'missing-position.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --animation walk --output $missingPosition --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $missingPosition)) { throw 'Animation without frame/time was accepted' }

$outOfRange = Join-Path $root 'out-of-range.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --animation walk --frame 10 --output $outOfRange --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outOfRange)) { throw 'Out-of-range animation frame was accepted' }

$duplicate = Join-Path $root 'duplicate.json'
$duplicateObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x902'; assets = $assets; animations = @($animations[0], $animations[0]) }
[IO.File]::WriteAllText($duplicate, ($duplicateObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$duplicateOutput = Join-Path $root 'duplicate-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $duplicate --animation walk --frame 0 --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $duplicateOutput)) { throw 'Duplicate animation clip was accepted' }

Write-Output "PREVIEW ANIMATION FIXTURES PASS $root"
