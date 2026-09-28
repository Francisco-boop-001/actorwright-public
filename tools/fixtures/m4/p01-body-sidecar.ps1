param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_body_sidecar.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\p01-body-sidecar-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'BodySlide sidecar evidence must remain under the project root.'
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

function Write-Json([string]$path, [string]$json) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

$fo4 = @'
{"version":1,"plugin":"GeneratedFixtureFO4.esp","npcs":{"Fallout4.esm|000800":{"editorId":"GeneratedFixtureFO4","bodyMorphs":{"Calf":"0.25","Waist":-0.1},"skinTemplateId":"SkinHuman","gender":"female","overlays":[{"template":"Textures\\Actors\\Character\\Overlay.dds","priority":1,"tint":[1,0.5,0.25,1],"offsetUV":[0,0],"scaleUV":[1,1]}]}}}
'@ -replace '"([0-9]+\.[0-9]+)"', '$1'
$sse = @'
{"version":11,"plugin":"GeneratedFixtureSSE.esp","npcs":{"Skyrim.esm|000800":{"editorId":"GeneratedFixtureSSE","bodyMorphs":{"Breast":"0.4"},"bodyMorphsKeyed":{"Breast":{"female":"0.5"}},"gender":"female","sseBodyOverlays":[{"node":"Body","diffuse":"textures\\body.dds","normal":"textures\\body_n.dds","tint":[1,1,1,1],"alpha":0.8}],"sseNodeTransforms":[{"node":"NPC Spine [Spn0]","s":1.0,"sm":1,"p":[0,0,0],"r":[0,0,0]}],"sseHairColor":1122867,"sseCustomMorphs":[{"name":"Jaw","value":0.2}],"sseSculpt":[{"index":3,"dx":0.1,"dy":0.0,"dz":-0.1}],"sseTintTextures":[{"index":0,"texture":"textures\\tint.dds"}]}}}
'@ -replace '"([0-9]+\.[0-9]+)"', '$1'

$fixtures = @(
    @{ Edition = 'fallout4'; Json = $fo4; Name = 'GeneratedFixtureFO4.bssliders' },
    @{ Edition = 'skyrimse'; Json = $sse; Name = 'GeneratedFixtureSSE.bssliders' }
)
foreach ($fixture in $fixtures) {
    $file = Join-Path $output $fixture.Name
    Write-Json $file $fixture.Json
    $evidence = Join-Path $output "$($fixture.Edition)-allowed.json"
    & $dotnet $cli body sidecar inspect --game $fixture.Edition --file $file --json |
        Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "BodySlide sidecar inspection failed for $($fixture.Edition)." }
    & $python $verifier --json $evidence --expect allowed
    if ($LASTEXITCODE -ne 0) { throw "Independent verifier rejected $($fixture.Edition) evidence." }
}

$duplicate = Join-Path $output 'GeneratedFixtureDuplicate.bssliders'
Write-Json $duplicate '{"version":1,"plugin":"GeneratedFixtureDuplicate.esp","plugin":"GeneratedFixtureDuplicate.esp","npcs":{}}'
$duplicateEvidence = Join-Path $output 'duplicate-refused.json'
& $dotnet $cli body sidecar inspect --game fallout4 --file $duplicate --json |
    Out-File -LiteralPath $duplicateEvidence -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Duplicate sidecar did not return validation failure.' }
& $python $verifier --json $duplicateEvidence --expect refused --reason body-sidecar-duplicate-key
if ($LASTEXITCODE -ne 0) { throw 'Independent verifier did not accept duplicate-key refusal.' }

$nonFinite = Join-Path $output 'GeneratedFixtureNonFinite.bssliders'
Write-Json $nonFinite '{"version":1,"plugin":"GeneratedFixtureNonFinite.esp","npcs":{"Fallout4.esm|000800":{"bodyMorphs":{"Waist":1e999}}}}'
$nonFiniteEvidence = Join-Path $output 'nonfinite-refused.json'
& $dotnet $cli body sidecar inspect --game fallout4 --file $nonFinite --json |
    Out-File -LiteralPath $nonFiniteEvidence -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Non-finite sidecar did not return validation failure.' }
& $python $verifier --json $nonFiniteEvidence --expect refused --reason body-sidecar-number
if ($LASTEXITCODE -ne 0) { throw 'Independent verifier did not accept non-finite refusal.' }

Write-Output "BODY SIDECAR FIXTURES PASS $output"
