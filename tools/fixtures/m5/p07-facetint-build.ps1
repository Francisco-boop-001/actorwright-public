$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facetint-build-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facetint_build.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$manifest = Join-Path $root 'build-manifest.json'
$manifestText = @'
{
  "schemaVersion": "1",
  "npcFormId": "0x00000800",
  "width": 512,
  "height": 512,
  "format": "bgra8",
  "mipCount": 1,
  "alphaMode": "preserve",
  "baseColor": [0.2, 0.4, 0.6, 0.5],
  "layers": [
    {"name":"complexion","source":"textures/complexion.dds","provider":"fixture","blend":"over","opacity":0.5,"color":[1,0,0,1]},
    {"name":"detail","source":"textures/detail.dds","provider":"fixture","blend":"multiply","opacity":0.25,"color":[0.5,1,1,1]}
  ],
  "probes": [{"x":0,"y":0},{"x":511,"y":511}]
}
'@
[IO.File]::WriteAllText($manifest, $manifestText, (New-Object Text.UTF8Encoding($false)))

$output = Join-Path $root 'fo4-tint-a.json'
$repeat = Join-Path $root 'fo4-tint-b.json'
$dds = Join-Path $root 'fo4-tint.dds'
$response = Join-Path $root 'fo4-response.json'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --npc 0x00000800 --output $output --dds-output $dds --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 FaceTint build failed' }
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --npc 0x00000800 --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated FO4 FaceTint build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response --dds $dds
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4 FaceTint verification failed' }

$badManifest = Join-Path $root 'bad-resolution.json'
$badText = $manifestText.Replace('"width": 512', '"width": 256').Replace('"height": 512', '"height": 256')
[IO.File]::WriteAllText($badManifest, $badText, (New-Object Text.UTF8Encoding($false)))
$badOutput = Join-Path $root 'bad-resolution-output.json'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $badManifest --output $badOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $badOutput)) { throw 'Unsupported FaceTint resolution was accepted' }

$badFormatOutput = Join-Path $root 'bad-format-output.json'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --format bc1 --output $badFormatOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $badFormatOutput)) { throw 'Unsupported FaceTint format was accepted' }

$bc3Output = Join-Path $root 'fo4-tint-bc3.json'
$bc3Response = Join-Path $root 'fo4-bc3-response.json'
$bc3Dds = Join-Path $root 'fo4-tint-bc3.dds'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --format bc3 --output $bc3Output --dds-output $bc3Dds --json | Out-File -LiteralPath $bc3Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Typed BC3 FaceTint format was rejected' }
$bc3Artifact = Get-Content -LiteralPath $bc3Output -Raw | ConvertFrom-Json
if ($bc3Artifact.format -ne 'bc3' -or -not (Test-Path -LiteralPath $bc3Dds)) { throw 'Typed BC3 format metadata or DDS output was not preserved' }
& python $verifier --manifest $manifest --output $bc3Output --repeat $bc3Output --response $bc3Response --dds $bc3Dds --dds-format bc3
if ($LASTEXITCODE -ne 0) { throw 'Independent BC3 FaceTint verification failed' }
$bc3RepeatOutput = Join-Path $root 'fo4-tint-bc3-repeat.json'
$bc3RepeatDds = Join-Path $root 'fo4-tint-bc3-repeat.dds'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --format bc3 --output $bc3RepeatOutput --dds-output $bc3RepeatDds --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated BC3 FaceTint build failed' }
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $bc3Hash = -join ($sha256.ComputeHash([IO.File]::ReadAllBytes($bc3Dds)) | ForEach-Object { $_.ToString('x2') })
    $bc3RepeatHash = -join ($sha256.ComputeHash([IO.File]::ReadAllBytes($bc3RepeatDds)) | ForEach-Object { $_.ToString('x2') })
}
finally { $sha256.Dispose() }
if ($bc3Hash -ne $bc3RepeatHash) { throw 'Repeated BC3 FaceTint DDS output was not deterministic' }

$bc7Output = Join-Path $root 'fo4-tint-bc7.json'
$bc7Response = Join-Path $root 'fo4-bc7-response.json'
$bc7Dds = Join-Path $root 'fo4-tint-bc7.dds'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --format bc7 --output $bc7Output --dds-output $bc7Dds --json | Out-File -LiteralPath $bc7Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Typed BC7 FaceTint format was rejected' }
$bc7Artifact = Get-Content -LiteralPath $bc7Output -Raw | ConvertFrom-Json
if ($bc7Artifact.format -ne 'bc7' -or -not (Test-Path -LiteralPath $bc7Dds)) { throw 'Typed BC7 format metadata or DDS output was not preserved' }
& python $verifier --manifest $manifest --output $bc7Output --repeat $bc7Output --response $bc7Response --dds $bc7Dds --dds-format bc7
if ($LASTEXITCODE -ne 0) { throw 'Independent BC7 FaceTint verification failed' }

$providerRoot = Join-Path $root 'providers'
New-Item -ItemType Directory -Force -Path (Join-Path $providerRoot 'textures') | Out-Null
Copy-Item -LiteralPath $bc3Dds -Destination (Join-Path $providerRoot 'textures\complexion.dds')
Copy-Item -LiteralPath $bc3Dds -Destination (Join-Path $providerRoot 'textures\detail.dds')
$sampledOutput = Join-Path $root 'fo4-tint-sampled.json'
$sampledResponse = Join-Path $root 'fo4-sampled-response.json'
$sampledDds = Join-Path $root 'fo4-tint-sampled.dds'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --provider-root $providerRoot --output $sampledOutput --dds-output $sampledDds --json | Out-File -LiteralPath $sampledResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Provider-bound FaceTint sampling was rejected' }
& python $verifier --manifest $manifest --output $sampledOutput --repeat $sampledOutput --response $sampledResponse --dds $sampledDds --dds-format bgra8 --provider-root $providerRoot
if ($LASTEXITCODE -ne 0) { throw 'Independent provider-bound FaceTint verification failed' }

$sseOutput = Join-Path $root 'sse-tint.json'
$sseResponse = Join-Path $root 'sse-response.json'
& $dotnet $cli facegen build-tint --edition skyrimse --manifest $manifest --output $sseOutput --json | Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE FaceTint build failed' }
& python $verifier --manifest $manifest --output $sseOutput --repeat $sseOutput --response $sseResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE FaceTint verification failed' }
$sseBc7Output = Join-Path $root 'sse-tint-bc7.json'
$sseBc7Response = Join-Path $root 'sse-bc7-response.json'
$sseBc7Dds = Join-Path $root 'sse-tint-bc7.dds'
& $dotnet $cli facegen build-tint --edition skyrimse --manifest $manifest --format bc7 --output $sseBc7Output --dds-output $sseBc7Dds --json | Out-File -LiteralPath $sseBc7Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE BC7 FaceTint build failed' }
& python $verifier --manifest $manifest --output $sseBc7Output --repeat $sseBc7Output --response $sseBc7Response --dds $sseBc7Dds --dds-format bc7
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE BC7 FaceTint verification failed' }

$existing = Join-Path $root 'existing.json'
Copy-Item -LiteralPath $output -Destination $existing
& $dotnet $cli facegen build-tint --edition fallout4 --manifest $manifest --output $existing --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'Existing FaceTint output was overwritten' }

$outside = Join-Path $root 'outside.json'
& $dotnet $cli facegen build-tint --edition fallout4 --manifest 'F:\ExampleGame\Data\missing-facetint.json' --output $outside --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $outside)) { throw 'Protected-root FaceTint manifest was accepted' }

Write-Output "FACETINT BUILD FIXTURES PASS $root"
