$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegeom_binary.py'
$assetRoot = Join-Path $project '01-source-copies\m9-facegen-assets\lumi'
$source = Join-Path $assetRoot 'femalehead.nif'
$root = Join-Path $project '03-builds\work\m9-facegeom-binary-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$morphs = Join-Path $root 'morphs.json'
[IO.File]::WriteAllText($morphs, '[{"name":"BrowDownLeft","value":0.35}]', [Text.UTF8Encoding]::new($false))

function Invoke-Bake([string]$edition, [string]$prefix) {
    $nif = Join-Path $root "$prefix.nif"
    $response = Join-Path $root "$prefix-response.json"
    & $dotnet $cli facegen build-geom-nif --edition $edition --asset-root $assetRoot --source $source `
        --output $nif --morphs "@$morphs" --json | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "FaceGeom binary bake failed for $edition" }
    & python $verifier --nif $nif --response $response --asset-root $assetRoot --source $source --edition $edition
    if ($LASTEXITCODE -ne 0) { throw "Independent FaceGeom binary verification failed for $edition" }
    return @{ Nif = $nif; Response = $response }
}

$sse = Invoke-Bake 'skyrimse' 'skyrimse'
$fo4 = Invoke-Bake 'fallout4' 'fallout4'

$existing = Join-Path $root 'existing-response.json'
$before = (Get-FileHash -LiteralPath $sse.Nif -Algorithm SHA256).Hash
$ErrorActionPreference = 'Continue'
& $dotnet $cli facegen build-geom-nif --edition skyrimse --asset-root $assetRoot --source $source `
    --output $sse.Nif --morphs "@$morphs" --json | Out-File -LiteralPath $existing -Encoding utf8
$existingExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($existingExit -eq 0) { throw 'FaceGeom binary route accepted an existing output' }
if ((Get-FileHash -LiteralPath $sse.Nif -Algorithm SHA256).Hash -ne $before) { throw 'Existing FaceGeom output changed' }

$zeroMorphs = Join-Path $root 'zero-morphs.json'
[IO.File]::WriteAllText($zeroMorphs, '[{"name":"BrowDownLeft","value":0.0}]', [Text.UTF8Encoding]::new($false))
$zeroResponse = Join-Path $root 'zero-response.json'
$zeroOutput = Join-Path $root 'zero.nif'
$ErrorActionPreference = 'Continue'
& $dotnet $cli facegen build-geom-nif --edition skyrimse --asset-root $assetRoot --source $source `
    --output $zeroOutput --morphs "@$zeroMorphs" --json | Out-File -LiteralPath $zeroResponse -Encoding utf8
$zeroExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($zeroExit -eq 0 -or (Test-Path -LiteralPath $zeroOutput)) { throw 'Zero morph bake was accepted' }

Write-Output "FACEGEOM BINARY FIXTURES PASS $root (FO4 target uses the same copied head/TRI asset and is not FO4 provider authority)"
