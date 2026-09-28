$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-variants-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_variants.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$assets = @(
    [ordered]@{ category = 'face'; path = 'meshes/face.nif'; provider = 'loose:face.nif'; sha256 = $hash }
    [ordered]@{ category = 'outfit'; path = 'meshes/outfit.nif'; provider = 'loose:outfit.nif'; sha256 = $hash }
    [ordered]@{ category = 'accessory'; path = 'meshes/ring.nif'; provider = 'loose:ring.nif'; sha256 = $hash }
)
$morphs = @(
    [ordered]@{ category = 'bone'; name = 'jaw'; value = 0.25 }
    [ordered]@{ category = 'sculpt'; name = 'nose'; value = -0.5 }
)
$variants = @(
    [ordered]@{
        id = 'battle'
        outfit = 'M2FixtureFO4.esp|0x801'
        assets = @('meshes/outfit.nif', 'meshes/ring.nif')
        morphs = @([ordered]@{ category = 'bone'; name = 'jaw'; value = 0.75 })
    }
)
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x800'; assets = $assets; morphs = $morphs; variants = $variants }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$output = Join-Path $root 'variant-a.json'
$repeat = Join-Path $root 'variant-b.json'
$response = Join-Path $root 'variant-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --outfit 'M2FixtureFO4.esp|0x801' --variant battle --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview variant evidence build failed' }
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --outfit 'M2FixtureFO4.esp|0x801' --variant battle --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview variant evidence build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent preview variant verification failed' }

$partial = Join-Path $root 'partial.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --outfit 'M2FixtureFO4.esp|0x801' --output $partial --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $partial)) { throw 'Partial preview variant selection was accepted' }

$mismatch = Join-Path $root 'mismatch.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --outfit 'M2FixtureFO4.esp|0x802' --variant battle --output $mismatch --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $mismatch)) { throw 'Mismatched preview variant outfit was accepted' }

$duplicateManifest = Join-Path $root 'duplicate-variant.json'
$duplicateManifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x800'; assets = $assets; morphs = $morphs; variants = @($variants[0], $variants[0]) }
[IO.File]::WriteAllText($duplicateManifest, ($duplicateManifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$duplicateOutput = Join-Path $root 'duplicate-variant-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $duplicateManifest --outfit 'M2FixtureFO4.esp|0x801' --variant battle --output $duplicateOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $duplicateOutput)) { throw 'Duplicate preview variant ID was accepted' }

$unknownManifest = Join-Path $root 'unknown-piece.json'
$unknownVariant = [ordered]@{ id = 'unknown'; outfit = 'M2FixtureFO4.esp|0x801'; assets = @('meshes/missing.nif'); morphs = @([ordered]@{ category = 'bone'; name = 'missing'; value = 0.1 }) }
$unknownManifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x800'; assets = $assets; morphs = $morphs; variants = @($unknownVariant) }
[IO.File]::WriteAllText($unknownManifest, ($unknownManifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$unknownOutput = Join-Path $root 'unknown-piece-output.json'
& $dotnet $cli preview render --edition fallout4 --manifest $unknownManifest --outfit 'M2FixtureFO4.esp|0x801' --variant unknown --output $unknownOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unknownOutput)) { throw 'Unknown preview variant pieces were accepted' }

Write-Output "PREVIEW VARIANT FIXTURES PASS $root"
