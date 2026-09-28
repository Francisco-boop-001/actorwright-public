$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m9-facegen-pack-plan-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$fixtureGenerator = Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_pack_plan.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Get-TreeFingerprint([string]$path) {
    $items = @(Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($path.Length).TrimStart('\', '/').Replace('\', '/')
        "$relative|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
    return $items -join "`n"
}

function Invoke-PackFixture([string]$edition, [string]$plugin, [bool]$debug, [bool]$sharedNeutral) {
    $case = Join-Path $root $edition
    $data = Join-Path $case 'Data'
    New-Item -ItemType Directory -Force -Path $case, $data | Out-Null
    $generated = Join-Path $case $plugin
    & $dotnet $fixtureGenerator '--headparts' $generated (Join-Path $case "$edition-unused.esp")
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed for $edition." }
    Copy-Item -LiteralPath $generated -Destination (Join-Path $data $plugin)

    $geom = Join-Path $data "Meshes\Actors\Character\FaceGenData\FaceGeom\$plugin"
    New-Item -ItemType Directory -Force -Path $geom | Out-Null
    $geomName = if ($debug) { '00000800_2.nif' } else { '00000800.nif' }
    Set-Content -LiteralPath (Join-Path $geom $geomName) -Value "facegeom-$edition" -NoNewline
    if ($edition -eq 'fallout4') {
        $custom = Join-Path $data "Textures\Actors\Character\FaceCustomization\$plugin"
        New-Item -ItemType Directory -Force -Path $custom | Out-Null
        foreach ($suffix in @('_d', '_msn', '_s')) {
            $name = if ($debug) { "00000800${suffix}_2.dds" } else { "00000800$suffix.dds" }
            Set-Content -LiteralPath (Join-Path $custom $name) -Value "custom$suffix" -NoNewline
        }
    } else {
        $tint = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceTint\$plugin"
        $diff = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceDiffuse\$plugin"
        $norm = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceNormal\$plugin"
        New-Item -ItemType Directory -Force -Path $tint, $diff, $norm | Out-Null
        $tintName = if ($debug) { '00000800_2.dds' } else { '00000800.dds' }
        Set-Content -LiteralPath (Join-Path $tint $tintName) -Value 'tint' -NoNewline
        if ($sharedNeutral) {
            $neutralName = if ($debug) { 'facedetailneutral_2.dds' } else { 'facedetailneutral.dds' }
            Set-Content -LiteralPath (Join-Path $tint $neutralName) -Value 'neutral' -NoNewline
        }
        # Optional per-NPC diffuse/normal are intentionally absent to prove optional handling.
    }

    $args = @('facegen', 'plan-pack', '--game', $edition, '--data-root', $data,
        '--plugins', $plugin, '--anchor-plugin', $plugin, '--npc', '0x800', '--json')
    if ($debug) { $args += '--debug-sandbox' }
    if ($sharedNeutral) { $args += '--shared-neutral-detail' }
    $before = Get-TreeFingerprint $data
    $response = Join-Path $case 'plan.json'
    & $dotnet $cli @args | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Pack plan failed for $edition." }
    if ((Get-TreeFingerprint $data) -cne $before) { throw "Pack plan mutated copied Data for $edition." }
    $verifyArgs = @('--json', $response, '--edition', $edition, '--data-root', $data, '--plugin', $plugin)
    if ($debug) { $verifyArgs += '--debug' }
    if ($sharedNeutral) { $verifyArgs += '--shared-neutral' }
    & python $verifier @verifyArgs
    if ($LASTEXITCODE -ne 0) { throw "Independent pack-plan verifier failed for $edition." }
}

Invoke-PackFixture 'fallout4' 'P09PackFO4.esp' $true $false
Invoke-PackFixture 'skyrimse' 'P09PackSSE.esp' $false $true

$missingCase = Join-Path $root 'missing-required'
New-Item -ItemType Directory -Force -Path $missingCase | Out-Null
$missingData = Join-Path $missingCase 'Data'
New-Item -ItemType Directory -Force -Path $missingData | Out-Null
$missingPlugin = Join-Path $missingCase 'P09Missing.esp'
& $dotnet $fixtureGenerator '--headparts' $missingPlugin (Join-Path $missingCase 'unused.esp')
Copy-Item -LiteralPath $missingPlugin -Destination (Join-Path $missingData (Split-Path $missingPlugin -Leaf))
$missingResponse = Join-Path $missingCase 'plan.json'
& $dotnet $cli facegen plan-pack --game fallout4 --data-root $missingData --plugins P09Missing.esp --anchor-plugin P09Missing.esp --npc 0x800 --json |
    Out-File -LiteralPath $missingResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Missing required FaceGen source did not return validation exit 4.' }

$duplicateCase = Join-Path $root 'duplicate-refusal'
New-Item -ItemType Directory -Force -Path $duplicateCase | Out-Null
& $dotnet $cli facegen plan-pack --game fallout4 --data-root $missingData --plugins P09Missing.esp,P09Missing.esp --anchor-plugin P09Missing.esp --npc 0x800 --json |
    Out-File -LiteralPath (Join-Path $duplicateCase 'duplicate.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Duplicate pack-plan load-order entries did not return validation exit 4.' }
$protectedCase = Join-Path $root 'protected-root-refusal'
New-Item -ItemType Directory -Force -Path $protectedCase | Out-Null
& $dotnet $cli facegen plan-pack --game fallout4 --data-root 'F:\ExampleGame\Data' --plugins P09Missing.esp --anchor-plugin P09Missing.esp --npc 0x800 --json |
    Out-File -LiteralPath (Join-Path $protectedCase 'protected-root.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Protected Data root did not return validation exit 4.' }
$anchorCase = Join-Path $root 'anchor-refusal'
New-Item -ItemType Directory -Force -Path $anchorCase | Out-Null
& $dotnet $cli facegen plan-pack --game fallout4 --data-root (Join-Path $root 'fallout4\Data') --plugins P09PackFO4.esp --anchor-plugin MissingAnchor.esp --npc 0x800 --json |
    Out-File -LiteralPath (Join-Path $anchorCase 'missing-anchor.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Missing anchor plugin did not return validation exit 4.' }
Write-Output "FACEGEN PACK-PLAN FIXTURES PASS $root"
