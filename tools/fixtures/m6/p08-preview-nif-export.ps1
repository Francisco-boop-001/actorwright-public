$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-nif-export-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_nif_export.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'
$assets = @(
    [ordered]@{ category = 'face'; path = 'meshes/face.nif'; provider = 'fixture'; sha256 = $hash }
)
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x903'; assets = $assets }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$scene = Join-Path $root 'scene-output.json'
$sceneResponse = Join-Path $root 'scene-response.json'
& $dotnet $cli preview render --edition fallout4 --manifest $manifest --output $scene --json | Out-File -LiteralPath $sceneResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview scene build failed' }
$plan = Join-Path $root 'npc.nif.plan.json'
$response = Join-Path $root 'export-response.json'
& $dotnet $cli preview export-nif --edition fallout4 --scene $scene --output $plan --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview NIF export plan failed' }
& python $verifier --scene $scene --plan $plan --response $response
if ($LASTEXITCODE -ne 0) { throw 'Independent preview NIF export verification failed' }

$binary = Join-Path $root 'npc.nif'
& $dotnet $cli preview export-nif --edition fallout4 --scene $scene --output $binary --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $binary)) { throw 'Binary NIF output was accepted' }

$outside = Join-Path $root 'outside-output.nif.plan.json'
$outsideScene = 'F:\ExampleGame\Data\missing-preview-scene.json'
& $dotnet $cli preview export-nif --edition fallout4 --scene $outsideScene --output $outside --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outside)) { throw 'Outside-K preview scene was accepted' }

$existingResponse = Join-Path $root 'existing-response.json'
$planHashBefore = (Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash
& $dotnet $cli preview export-nif --edition fallout4 --scene $scene --output $plan --json | Out-File -LiteralPath $existingResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'Existing preview NIF plan was overwritten' }
if ((Get-FileHash -LiteralPath $plan -Algorithm SHA256).Hash -ne $planHashBefore) { throw 'Existing plan hash changed' }

Write-Output "PREVIEW NIF EXPORT FIXTURES PASS $root"
