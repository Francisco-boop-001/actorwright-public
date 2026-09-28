$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_nif_binary.py'
$assetRoot = Join-Path $project '01-source-copies\m6-preview-assets\sse'
$manifest = Join-Path $project 'tools\fixtures\m6\p08-preview-scene-sse.json'
$root = Join-Path $project '03-builds\work\m6-preview-nif-binary-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Invoke-BinaryExport([string]$edition, [string]$sceneManifest, [string]$prefix, [string]$assetRootForCase = $assetRoot) {
    $scene = Join-Path $root "$prefix-scene.json"
    $sceneResponse = Join-Path $root "$prefix-scene-response.json"
    $image = Join-Path $root "$prefix-scene.png"
    & $dotnet $cli preview render --edition $edition --manifest $sceneManifest --output $scene `
        --asset-root $assetRootForCase --image-output $image --width 64 --height 64 --json |
        Out-File -LiteralPath $sceneResponse -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Preview scene build failed for $edition" }
    $nif = Join-Path $root "$prefix.nif"
    $response = Join-Path $root "$prefix-response.json"
    & $dotnet $cli preview export-nif --edition $edition --scene $scene --asset-root $assetRootForCase `
        --output $nif --json | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Binary NIF export failed for $edition" }
    & python $verifier --nif $nif --scene $scene --response $response --edition $edition
    if ($LASTEXITCODE -ne 0) { throw "Independent binary NIF verification failed for $edition" }
    return @{ Scene = $scene; Nif = $nif; Response = $response }
}

$sse = Invoke-BinaryExport 'skyrimse' $manifest 'skyrimse'

$fo4Manifest = Join-Path $root 'fo4-manifest.json'
$manifestText = Get-Content -LiteralPath $manifest -Raw
[IO.File]::WriteAllText($fo4Manifest, $manifestText.Replace('"skyrimse"', '"fallout4"'), [Text.UTF8Encoding]::new($false))
$fo4 = Invoke-BinaryExport 'fallout4' $fo4Manifest 'fallout4'

$existingResponse = Join-Path $root 'existing-output-response.json'
$hashBefore = (Get-FileHash -LiteralPath $sse.Nif -Algorithm SHA256).Hash
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview export-nif --edition skyrimse --scene $sse.Scene --asset-root $assetRoot `
    --output $sse.Nif --json | Out-File -LiteralPath $existingResponse -Encoding utf8
$existingExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($existingExit -eq 0) { throw 'Binary NIF exporter accepted an existing output' }
if ((Get-FileHash -LiteralPath $sse.Nif -Algorithm SHA256).Hash -ne $hashBefore) { throw 'Existing NIF changed' }

$outsideResponse = Join-Path $root 'outside-response.json'
$outsideRoot = Join-Path $root 'outside-assets'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview export-nif --edition skyrimse --scene $sse.Scene --asset-root 'F:\ExampleGame\Data' `
    --output (Join-Path $root 'outside.nif') --json | Out-File -LiteralPath $outsideResponse -Encoding utf8
$outsideExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($outsideExit -eq 0 -or (Test-Path -LiteralPath (Join-Path $root 'outside.nif'))) { throw 'Outside-K asset root was accepted' }

$badScene = Join-Path $root 'bad-hash-scene.json'
$badText = $manifestText.Replace('3D33BCC7A0EB04D88C30AB4A4E5120FDB7ACDC8B5646AF7728825BBDEAFE0A83', ('0' * 64))
[IO.File]::WriteAllText($badScene, $badText, [Text.UTF8Encoding]::new($false))
$badResponse = Join-Path $root 'bad-hash-response.json'
$badOutput = Join-Path $root 'bad-hash.nif'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview export-nif --edition skyrimse --scene $badScene --asset-root $assetRoot `
    --output $badOutput --json | Out-File -LiteralPath $badResponse -Encoding utf8
$badExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($badExit -eq 0 -or (Test-Path -LiteralPath $badOutput)) { throw 'Source hash mismatch was accepted' }

$morphAssetRoot = Join-Path $project '01-source-copies\m9-facegen-assets\lumi'
$morphNif = Join-Path $morphAssetRoot 'femalehead.nif'
$morphHash = (Get-FileHash -LiteralPath $morphNif -Algorithm SHA256).Hash.ToLowerInvariant()
$morphManifest = Join-Path $root 'morph-scene-manifest.json'
$morphObject = [ordered]@{
    schemaVersion = 1; edition = 'skyrimse'; npcFormId = '0x902'
    assets = @([ordered]@{ category = 'face'; path = 'femalehead.nif'; provider = 'fixture'; sha256 = $morphHash })
    morphs = @([ordered]@{ category = 'vertex'; name = 'Aah'; value = 0.4 })
}
[IO.File]::WriteAllText($morphManifest, ($morphObject | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$morph = Invoke-BinaryExport 'skyrimse' $morphManifest 'morph-skyrimse' $morphAssetRoot
$morphFo4Manifest = Join-Path $root 'morph-fo4-manifest.json'
$morphFo4Text = Get-Content -LiteralPath $morphManifest -Raw
[IO.File]::WriteAllText($morphFo4Manifest, $morphFo4Text.Replace('"skyrimse"', '"fallout4"'), [Text.UTF8Encoding]::new($false))
$morphFo4 = Invoke-BinaryExport 'fallout4' $morphFo4Manifest 'morph-fallout4' $morphAssetRoot

$zeroScene = Join-Path $root 'zero-morph-scene.json'
$zeroSceneText = Get-Content -LiteralPath $morph.Scene -Raw
[IO.File]::WriteAllText($zeroScene, $zeroSceneText.Replace('"value": 0.4', '"value": 0.0'), [Text.UTF8Encoding]::new($false))
$zeroResponse = Join-Path $root 'zero-morph-response.json'
$zeroOutput = Join-Path $root 'zero-morph.nif'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview export-nif --edition skyrimse --scene $zeroScene --asset-root $morphAssetRoot `
    --output $zeroOutput --json | Out-File -LiteralPath $zeroResponse -Encoding utf8
$zeroExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($zeroExit -eq 0 -or (Test-Path -LiteralPath $zeroOutput)) { throw 'Zero morph NIF export was accepted' }

Write-Output "PREVIEW NIF BINARY FIXTURES PASS $root"
