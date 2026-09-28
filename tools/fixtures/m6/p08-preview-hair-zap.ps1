$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_hair_zap.py'
$assetRoot = Join-Path $project '01-source-copies\m6-preview-assets\sse'
$root = Join-Path $project '03-builds\work\m6-preview-hair-zap-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$hash = (Get-FileHash -LiteralPath (Join-Path $assetRoot 'hair.nif') -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $root 'scene.json'
$json = [ordered]@{
    schemaVersion = 1; edition = 'skyrimse'; npcFormId = '0x902'
    assets = @([ordered]@{ category = 'hair'; path = 'hair.nif'; provider = 'fixture'; sha256 = $hash })
}
[IO.File]::WriteAllText($manifest, ($json | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

function Invoke-HairZapRender([string]$prefix, [string]$slots, [string]$headwear) {
    $scene = Join-Path $root "$prefix-scene.json"
    $image = Join-Path $root "$prefix.png"
    $response = Join-Path $root "$prefix-response.json"
    & $dotnet $cli preview render --edition skyrimse --manifest $manifest --render-headwear $headwear `
        --hair-slots $slots --output $scene --asset-root $assetRoot --image-output $image --width 128 --height 128 --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Hair-zap preview render failed for $prefix" }
    & python $verifier --scene $scene --image $image --mode $prefix | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Independent hair-zap verification failed for $prefix" }
    return $scene
}

$allScene = Invoke-HairZapRender 'all' '31,41' 'true'
$partialScene = Invoke-HairZapRender 'partial' '31' 'true'
$disabledScene = Invoke-HairZapRender 'disabled' '31' 'false'
& python $verifier --scene $allScene --image (Join-Path $root 'all.png') --mode all --compare $disabledScene
if ($LASTEXITCODE -ne 0) { throw 'Hair-zap and disabled renders were not independently distinguishable' }

$nifVerifier = Join-Path $project 'tools\verification\verify_preview_nif_binary.py'
foreach ($case in @(
    @{ Prefix = 'all'; Scene = $allScene; ExpectedApplied = $true },
    @{ Prefix = 'disabled'; Scene = $disabledScene; ExpectedApplied = $false }
)) {
    $nif = Join-Path $root "$($case.Prefix).nif"
    $response = Join-Path $root "$($case.Prefix)-nif-response.json"
    & $dotnet $cli preview export-nif --edition skyrimse --scene $case.Scene --asset-root $assetRoot `
        --output $nif --json | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Hair-zap NIF export failed for $($case.Prefix)" }
    & python $nifVerifier --nif $nif --scene $case.Scene --response $response --edition skyrimse | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Independent hair-zap NIF verification failed for $($case.Prefix)" }
    $nifPayload = Get-Content -LiteralPath $response -Raw | ConvertFrom-Json
    if ([bool]$nifPayload.hairZapApplied -ne [bool]$case.ExpectedApplied) {
        throw "Hair-zap NIF status drifted for $($case.Prefix)"
    }
}

$fo4Manifest = Join-Path $root 'fo4-scene.json'
[IO.File]::WriteAllText($fo4Manifest, ((Get-Content -LiteralPath $manifest -Raw).Replace('skyrimse', 'fallout4')), [Text.UTF8Encoding]::new($false))
$fo4Image = Join-Path $root 'fo4.png'
$fo4Scene = Join-Path $root 'fo4-scene-output.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli preview render --edition fallout4 --manifest $fo4Manifest --render-headwear true --hair-slots 30,31 `
    --output $fo4Scene --asset-root $assetRoot --image-output $fo4Image --width 128 --height 128 --json | Out-Null
$fo4Exit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($fo4Exit -eq 0 -or (Test-Path -LiteralPath $fo4Image)) { throw 'FO4 hair-zap silently accepted an asset without FO4 partition groups' }

$blender = Join-Path $workspace 'tools\external\blender-4.5.1-windows-x64\blender-4.5.1-windows-x64\blender.exe'
$generator = Join-Path $project 'tools\fixtures\m6\generate_fo4_hair_fixture.py'
$fo4Asset = Join-Path $root 'fo4-hair.nif'
$fo4Status = Join-Path $root 'fo4-generator-status.json'
$env:BLENDER_USER_CONFIG = Join-Path $workspace 'tools\external\blender-4.5.1-pynifly-profile\config'
$env:BLENDER_USER_SCRIPTS = Join-Path $workspace 'tools\external\blender-4.5.1-pynifly-profile\scripts'
$env:BLENDER_USER_DATA = Join-Path $workspace 'tools\external\blender-4.5.1-pynifly-profile\data'
& $blender --background --python $generator -- --source (Join-Path $assetRoot 'hair.nif') --output $fo4Asset --status $fo4Status | Out-Null
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $fo4Asset)) { throw 'FO4-targeted hair fixture generation failed' }
$fo4Hash = (Get-FileHash -LiteralPath $fo4Asset -Algorithm SHA256).Hash.ToLowerInvariant()
$fo4PositiveManifest = Join-Path $root 'fo4-positive-manifest.json'
$fo4PositiveObject = [ordered]@{
    schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x903'
    assets = @([ordered]@{ category = 'hair'; path = 'fo4-hair.nif'; provider = 'synthetic-fo4-contract'; sha256 = $fo4Hash })
}
[IO.File]::WriteAllText($fo4PositiveManifest, ($fo4PositiveObject | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$fo4PositiveScene = Join-Path $root 'fo4-positive-scene.json'
$fo4PositiveImage = Join-Path $root 'fo4-positive.png'
$fo4PositiveResponse = Join-Path $root 'fo4-positive-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $fo4PositiveManifest --render-headwear true --hair-slots 30,31 `
    --output $fo4PositiveScene --asset-root $root --image-output $fo4PositiveImage --width 128 --height 128 --json |
    Out-File -LiteralPath $fo4PositiveResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4-targeted hair-zap preview render failed' }
& python $verifier --scene $fo4PositiveScene --image $fo4PositiveImage --mode fo4 --edition fallout4 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4-targeted hair-zap verification failed' }
$fo4PositiveNif = Join-Path $root 'fo4-positive.nif'
$fo4PositiveNifResponse = Join-Path $root 'fo4-positive-nif-response.json'
& $dotnet $cli preview export-nif --edition fallout4 --scene $fo4PositiveScene --asset-root $root `
    --output $fo4PositiveNif --json | Out-File -LiteralPath $fo4PositiveNifResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4-targeted hair-zap NIF export failed' }
& python $nifVerifier --nif $fo4PositiveNif --scene $fo4PositiveScene --response $fo4PositiveNifResponse --edition fallout4 | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4-targeted hair-zap NIF verification failed' }
$fo4NifPayload = Get-Content -LiteralPath $fo4PositiveNifResponse -Raw | ConvertFrom-Json
if (!$fo4NifPayload.hairZapApplied -or !$fo4NifPayload.hairZap.topCovered -or !$fo4NifPayload.hairZap.longCovered) {
    throw 'FO4-targeted NIF hair-zap status was not applied'
}
Write-Output "PREVIEW HAIR-ZAP FIXTURE PASS $root"
