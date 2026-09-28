$ErrorActionPreference = 'Stop'
$project = 'K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation'
$workspace = 'K:\\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\\external\\dotnet-sdk-10.0.301-win-x64\\dotnet.exe'
$cli = Join-Path $project 'src\\NpcManager.Cli\\bin\\Release\\net10.0\\npcm.dll'
$verifier = Join-Path $project 'tools\\verification\\verify_preview_face_cull.py'
$assetRoot = Join-Path $project '01-source-copies\\m6-preview-assets\\sse'
$root = Join-Path $project '03-builds\\work\\m6-preview-face-cull-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$asset = Join-Path $assetRoot 'hair.nif'
$faceAsset = Join-Path $root 'face.nif'
$bodyAsset = Join-Path $root 'body.nif'
Copy-Item -LiteralPath $asset -Destination $faceAsset
Copy-Item -LiteralPath $asset -Destination $bodyAsset
$hash = (Get-FileHash -LiteralPath $faceAsset -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $root 'scene.json'
$payload = [ordered]@{
    schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x919'
    assets = @(
        [ordered]@{ category = 'face'; path = 'face.nif'; provider = 'synthetic-facegen-head'; sha256 = $hash }
        [ordered]@{ category = 'body'; path = 'body.nif'; provider = 'synthetic-body'; sha256 = $hash }
    )
}
[IO.File]::WriteAllText($manifest, ($payload | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$scene = Join-Path $root 'face-cull-scene.json'
$image = Join-Path $root 'face-cull.png'
$response = Join-Path $root 'face-cull-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --render-headwear true --hair-slots 32 `
    --output $scene --asset-root $root --image-output $image --width 128 --height 128 --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 slot-32 preview render failed' }
& python $verifier --scene $scene --image $image | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Independent slot-32 render verification failed' }
Write-Output "PREVIEW FACE-CULL FIXTURE PASS $root"
