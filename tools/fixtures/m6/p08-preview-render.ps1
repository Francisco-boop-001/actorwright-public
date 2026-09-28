$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$dotnet = 'K:\ExampleWorkspace\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_render.py'
$assetRoot = Join-Path $project '01-source-copies\m6-preview-assets\sse'
$scene = Join-Path $project 'tools\fixtures\m6\p08-preview-scene-sse.json'
$root = Join-Path $project '03-builds\work\m6-preview-render-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Invoke-Preview([string]$edition, [string]$manifest, [string]$prefix) {
    $semantic = Join-Path $root "$prefix-scene.json"
    $image = Join-Path $root "$prefix.png"
    $response = Join-Path $root "$prefix-response.json"
    & $dotnet $cli preview render --edition $edition --manifest $manifest --output $semantic `
        --asset-root $assetRoot --image-output $image --width 256 --height 256 --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Preview render failed for $edition" }
    & python $verifier --png $image --status $semantic --width 256 --height 256 --edition $edition
    if ($LASTEXITCODE -ne 0) { throw "Independent preview verification failed for $edition" }
    return @{ Semantic = $semantic; Image = $image }
}

$sse = Invoke-Preview 'skyrimse' $scene 'sse'
$existingSemantic = Join-Path $root 'existing-image-scene.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition skyrimse --manifest $scene --output $existingSemantic `
    --asset-root $assetRoot --image-output $sse.Image --width 256 --height 256 --json | Out-Null
$existingExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($existingExit -eq 0) { throw 'Preview renderer accepted an existing image output' }
$repeat = Invoke-Preview 'skyrimse' $scene 'sse-repeat'
& python $verifier --png $sse.Image --repeat $repeat.Image --status $sse.Semantic --width 256 --height 256 --edition skyrimse
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview render was not deterministic' }

$fo4Scene = Join-Path $root 'fo4-scene-manifest.json'
$text = Get-Content -LiteralPath $scene -Raw
[IO.File]::WriteAllText($fo4Scene, $text.Replace('"skyrimse"', '"fallout4"'), [Text.UTF8Encoding]::new($false))
$fo4 = Invoke-Preview 'fallout4' $fo4Scene 'fo4'

$usage = Join-Path $root 'usage.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition skyrimse --manifest $scene --output (Join-Path $root 'usage-scene.json') `
    --asset-root $assetRoot --json | Out-File -LiteralPath $usage -Encoding utf8
$usageExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($usageExit -ne 2) { throw 'Preview image options did not enforce the paired output contract' }
$dimensionUsage = Join-Path $root 'dimension-usage.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition skyrimse --manifest $scene --output (Join-Path $root 'dimension-usage-scene.json') `
    --width 256 --json | Out-File -LiteralPath $dimensionUsage -Encoding utf8
$dimensionExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($dimensionExit -ne 2) { throw 'Preview dimensions were accepted without an image route' }

$badScene = Join-Path $root 'bad-hash-scene.json'
$badText = $text.Replace('3D33BCC7A0EB04D88C30AB4A4E5120FDB7ACDC8B5646AF7728825BBDEAFE0A83', ('0' * 64))
[IO.File]::WriteAllText($badScene, $badText, [Text.UTF8Encoding]::new($false))
$badImage = Join-Path $root 'bad-hash.png'
& $dotnet $cli preview render --edition fallout4 --manifest $badScene --output (Join-Path $root 'bad-hash-scene-output.json') `
    --asset-root $assetRoot --image-output $badImage --width 256 --height 256 --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $badImage)) { throw 'Preview renderer accepted a source hash mismatch' }

Write-Output "PREVIEW RENDER FIXTURES PASS $root"
