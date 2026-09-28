$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_animation_render.py'
$assetRoot = Join-Path $project '01-source-copies\m6-preview-animation-assets\sse'
$root = Join-Path $project '03-builds\work\m6-preview-animation-render-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$hash = (Get-FileHash -LiteralPath (Join-Path $assetRoot 'femalehead.nif') -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $root 'animation-scene.json'
$json = [ordered]@{
    schemaVersion = 1; edition = 'skyrimse'; npcFormId = '0x902'
    assets = @([ordered]@{ category = 'face'; path = 'femalehead.nif'; provider = 'fixture'; sha256 = $hash })
    animations = @([ordered]@{ id = 'idle'; path = 'hww0_mt_idle.hkx'; skeleton = 'skeleton_female_sse.hkx'; frames = 275; fps = 30; additive = $false })
}
[IO.File]::WriteAllText($manifest, ($json | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

function Invoke-AnimationRender([string]$edition, [string]$manifestPath, [string]$prefix) {
    $scene = Join-Path $root "$prefix-scene.json"
    $image = Join-Path $root "$prefix.png"
    $response = Join-Path $root "$prefix-response.json"
    & $dotnet $cli preview render --edition $edition --manifest $manifestPath --animation idle --frame 1 `
        --output $scene --asset-root $assetRoot --image-output $image --width 128 --height 128 --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Animation preview render failed for $edition" }
    & python $verifier --scene $scene --image $image --edition $edition
    if ($LASTEXITCODE -ne 0) { throw "Independent animation preview verification failed for $edition" }
}

Invoke-AnimationRender 'skyrimse' $manifest 'sse'
$fo4Manifest = Join-Path $root 'fo4-animation-scene.json'
$text = Get-Content -LiteralPath $manifest -Raw
[IO.File]::WriteAllText($fo4Manifest, $text.Replace('"skyrimse"', '"fallout4"'), [Text.UTF8Encoding]::new($false))
Invoke-AnimationRender 'fallout4' $fo4Manifest 'fo4'

$badManifest = Join-Path $root 'missing-animation-scene.json'
[IO.File]::WriteAllText($badManifest, $text.Replace('hww0_mt_idle.hkx', 'missing.hkx'), [Text.UTF8Encoding]::new($false))
$badOutput = Join-Path $root 'missing.png'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition skyrimse --manifest $badManifest --animation idle --frame 1 `
    --output (Join-Path $root 'missing-scene.json') --asset-root $assetRoot --image-output $badOutput --width 128 --height 128 --json | Out-Null
$badExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($badExit -eq 0 -or (Test-Path -LiteralPath $badOutput)) { throw 'Missing HKX animation was accepted' }
$additiveManifest = Join-Path $root 'additive-animation-scene.json'
$additiveText = [Regex]::Replace($text, '("additive"\s*:\s*)false', '${1}true')
[IO.File]::WriteAllText($additiveManifest, $additiveText, [Text.UTF8Encoding]::new($false))
$additiveOutput = Join-Path $root 'additive.png'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition skyrimse --manifest $additiveManifest --animation idle --frame 1 `
    --output (Join-Path $root 'additive-scene.json') --asset-root $assetRoot --image-output $additiveOutput --width 128 --height 128 --json | Out-Null
$additiveExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($additiveExit -eq 0 -or (Test-Path -LiteralPath $additiveOutput)) { throw 'Additive HKX animation was silently accepted' }
Write-Output "PREVIEW ANIMATION RENDER FIXTURE PASS $root"
