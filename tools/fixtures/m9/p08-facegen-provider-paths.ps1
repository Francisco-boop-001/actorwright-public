$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m9-facegen-provider-paths-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$fixtureGenerator = Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_provider_paths.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Invoke-ProviderFixture([string]$edition, [string]$plugin) {
    $case = Join-Path $root $edition
    $data = Join-Path $case 'Data'
    New-Item -ItemType Directory -Force -Path $case, $data | Out-Null
    $generated = Join-Path $case $plugin
    & $dotnet $fixtureGenerator '--headparts' $generated (Join-Path $case "$edition-unused.esp")
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed for $edition." }
    Copy-Item -LiteralPath $generated -Destination (Join-Path $data $plugin)

    $geom = Join-Path $data "Meshes\Actors\Character\FaceGenData\FaceGeom\$plugin"
    New-Item -ItemType Directory -Force -Path $geom | Out-Null
    Set-Content -LiteralPath (Join-Path $geom '00000800.nif') -Value "facegeom-$edition" -NoNewline
    if ($edition -eq 'fallout4') {
        $custom = Join-Path $data "Textures\Actors\Character\FaceCustomization\$plugin"
        New-Item -ItemType Directory -Force -Path $custom | Out-Null
        foreach ($suffix in @('_d', '_msn', '_s')) {
            Set-Content -LiteralPath (Join-Path $custom "00000800$suffix.dds") -Value "custom$suffix" -NoNewline
        }
    } else {
        $tint = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceTint\$plugin"
        $diff = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceDiffuse\$plugin"
        $norm = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceNormal\$plugin"
        New-Item -ItemType Directory -Force -Path $tint, $diff, $norm | Out-Null
        Set-Content -LiteralPath (Join-Path $tint '00000800.dds') -Value 'tint' -NoNewline
        Set-Content -LiteralPath (Join-Path $diff '00000800.dds') -Value 'diffuse' -NoNewline
        Set-Content -LiteralPath (Join-Path $norm '00000800.dds') -Value 'normal' -NoNewline
    }

    $response = Join-Path $case 'resolve.json'
    & $dotnet $cli facegen resolve-providers --game $edition --data-root $data --plugins $plugin --npc 0x800 --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Provider resolver failed for $edition." }
    & python $verifier --json $response --edition $edition --data-root $data --plugin $plugin
    if ($LASTEXITCODE -ne 0) { throw "Independent provider verifier failed for $edition." }
}

Invoke-ProviderFixture 'fallout4' 'P08ProviderFO4.esp'
Invoke-ProviderFixture 'skyrimse' 'P08ProviderSSE.esp'
$usageRoot = Join-Path $root 'usage-refusal'
New-Item -ItemType Directory -Force -Path $usageRoot | Out-Null
$usageData = Join-Path $root 'fallout4\Data'
& $dotnet $cli facegen resolve-providers --game fallout4 --data-root $usageData --npc 0x800 --json |
    Out-File -LiteralPath (Join-Path $usageRoot 'missing-plugins.json') -Encoding utf8
if ($LASTEXITCODE -ne 2) { throw 'Missing explicit load order did not return usage exit 2.' }
$duplicateRoot = Join-Path $root 'duplicate-refusal'
New-Item -ItemType Directory -Force -Path $duplicateRoot | Out-Null
$duplicateData = Join-Path $root 'fallout4\Data'
& $dotnet $cli facegen resolve-providers --game fallout4 --data-root $duplicateData --plugins P08ProviderFO4.esp,P08ProviderFO4.esp --npc 0x800 --json |
    Out-File -LiteralPath (Join-Path $duplicateRoot 'duplicate-plugins.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Duplicate explicit load-order entries did not return validation exit 4.' }
Write-Output "FACEGEN PROVIDER-PATH FIXTURES PASS $root"
