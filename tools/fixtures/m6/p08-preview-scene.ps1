$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-scene-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_scene.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$assets = @(
    [ordered]@{ category = 'face'; path = 'meshes/face.nif'; provider = 'loose:face.nif'; sha256 = $hash }
    [ordered]@{ category = 'body'; path = 'meshes/body.nif'; provider = 'archive:body.ba2'; sha256 = $hash }
    [ordered]@{ category = 'hair'; path = 'meshes/hair.nif'; provider = 'loose:hair.nif'; sha256 = $hash }
    [ordered]@{ category = 'outfit'; path = 'meshes/outfit.nif'; provider = 'loose:outfit.nif'; sha256 = $hash }
    [ordered]@{ category = 'accessory'; path = 'meshes/amulet.nif'; provider = 'archive:armor.ba2'; sha256 = $hash }
)
$morphs = @(
    [ordered]@{ category = 'bone'; name = 'jaw'; value = 0.25 }
    [ordered]@{ category = 'sculpt'; name = 'nose'; value = -0.5 }
)
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x800'; assets = $assets; morphs = $morphs }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress), $utf8)
$output = Join-Path $root 'scene-a.json'
$repeat = Join-Path $root 'scene-b.json'
$visibleOutput = Join-Path $root 'scene-visible.json'
$morphOutput = Join-Path $root 'scene-morph.json'
$response = Join-Path $root 'scene-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview scene evidence build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview scene evidence build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --visible face,hair --output $visibleOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Preview visibility toggle build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --morphs bone --output $morphOutput --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Preview morph toggle build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response --visible-output $visibleOutput --morph-output $morphOutput
if ($LASTEXITCODE -ne 0) { throw 'Independent preview scene verification failed' }

$unknownOutput = Join-Path $root 'unknown-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --visible cape --output $unknownOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $unknownOutput)) { throw 'Unknown preview visibility category was accepted' }

$duplicate = Join-Path $root 'duplicate.json'
$duplicateObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x800'; assets = @($assets[0], $assets[0]) }
[IO.File]::WriteAllText($duplicate, ($duplicateObject | ConvertTo-Json -Compress), $utf8)
$duplicateOutput = Join-Path $root 'duplicate-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $duplicate --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $duplicateOutput)) { throw 'Duplicate preview asset path was accepted' }

$outsideOutput = Join-Path $root 'outside-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest 'F:\ExampleGame\Data\missing-preview.json' --output $outsideOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outsideOutput)) { throw 'Protected-root preview manifest was accepted' }

Write-Output "PREVIEW SCENE FIXTURES PASS $root"
