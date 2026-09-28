$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facegeom-build-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegeom_build.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$manifest = Join-Path $root 'build-manifest.json'
$output = Join-Path $root 'facegeom-a.json'
$repeat = Join-Path $root 'facegeom-b.json'
$response = Join-Path $root 'response.json'
$fixture = Join-Path $project '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$text = [IO.File]::ReadAllText($fixture).Trim()
$text = $text.Substring(0, $text.Length - 1) + ',"headParts":[{"editorId":"HumanHead","partType":1,"meshPath":"meshes/actors/character/characterassets/head.nif"}],"morphs":[{"name":"JawShape","value":0.25}],"textureRoutes":[{"slot":"diffuse","path":"textures/actors/character/facegen/m5_d.dds","provider":"fixture"}]}'
[IO.File]::WriteAllText($manifest, $text, (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli facegen build-geom --edition fallout4 --manifest $manifest --npc 0x00000800 --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FaceGeom build failed' }
& $dotnet $cli facegen build-geom --edition fallout4 --manifest $manifest --npc 0x00000800 --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated FaceGeom build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent FaceGeom build verification failed' }

$sseManifest = Join-Path $root 'sse-build-manifest.json'
$sseOutput = Join-Path $root 'sse-facegeom.json'
$sseRepeat = Join-Path $root 'sse-facegeom-repeat.json'
$sseResponse = Join-Path $root 'sse-response.json'
$sseFixture = Join-Path $project '01-source-copies\m5-fixtures\sse-facegen-valid.json'
$sseText = [IO.File]::ReadAllText($sseFixture).Trim()
$sseText = $sseText.Substring(0, $sseText.Length - 1) + ',"headParts":[{"editorId":"NordHead","partType":1,"meshPath":"meshes/actors/character/characterassets/head.nif"}],"morphs":[{"name":"NoseLength","value":-0.1}],"textureRoutes":[{"slot":"headNormal","path":"textures/actors/character/facegen/m5_msn.dds","provider":"fixture"}]}'
[IO.File]::WriteAllText($sseManifest, $sseText, (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli facegen build-geom --edition skyrimse --manifest $sseManifest --npc 0x01000800 --output $sseOutput --json | Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE FaceGeom build failed' }
& $dotnet $cli facegen build-geom --edition skyrimse --manifest $sseManifest --npc 0x01000800 --output $sseRepeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated SSE FaceGeom build failed' }
& python $verifier --manifest $sseManifest --output $sseOutput --repeat $sseRepeat --response $sseResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE FaceGeom build verification failed' }

$zeroOutput = Join-Path $root 'zero.json'
$zero = Join-Path $project '01-source-copies\m5-fixtures\zero-shapes.json'
& $dotnet $cli facegen build-geom --edition fallout4 --manifest $zero --output $zeroOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $zeroOutput)) { throw 'Zero-shape FaceGeom build was accepted' }

$outsideOutput = Join-Path $root 'outside.json'
& $dotnet $cli facegen build-geom --edition fallout4 --manifest 'F:\ExampleGame\Data\missing-facegen.json' --output $outsideOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outsideOutput)) { throw 'Protected-root FaceGeom manifest was accepted' }

Write-Output "FACEGEOM BUILD FIXTURES PASS $root"
