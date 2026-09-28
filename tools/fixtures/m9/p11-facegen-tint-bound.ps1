$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m9-facegen-tint-bound-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$fixtureGenerator = Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_tint_bound.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Get-TreeFingerprint([string]$path) {
    @(Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($path.Length).TrimStart('\', '/').Replace('\', '/')
        "$relative|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object) -join "`n"
}

function Invoke-BoundFixture([string]$edition, [string]$plugin, [string]$canonical) {
    $case = Join-Path $root $edition
    $data = Join-Path $case 'Data'
    $outputRoot = Join-Path $case 'OutputData'
    New-Item -ItemType Directory -Force -Path $case, $data, $outputRoot | Out-Null
    $generated = Join-Path $case $plugin
    & $dotnet $fixtureGenerator '--headparts' $generated (Join-Path $case "$edition-unused.esp")
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed for $edition." }
    Copy-Item -LiteralPath $generated -Destination (Join-Path $data $plugin)

    $source = Join-Path $data ($canonical.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $source) | Out-Null
    $manifest = Join-Path $case 'manifest.json'
    @{
        schemaVersion = '1'; npcFormId = '0x00000800'; width = 512; height = 512
        format = 'bgra8'; mipCount = 1; alphaMode = 'preserve'; baseColor = @(0.1, 0.2, 0.3, 1.0)
        layers = @(@{ name = 'canonical-provider'; source = $canonical; provider = $plugin; blend = 'replace'; opacity = 1.0; color = @(1.0, 1.0, 1.0, 1.0) })
        probes = @(@{ x = 0; y = 0 }, @{ x = 511; y = 511 })
    } | ConvertTo-Json -Depth 6 | ForEach-Object {
        [System.IO.File]::WriteAllText($manifest, $_, [System.Text.UTF8Encoding]::new($false))
    }

    # Produce one valid BGRA8 source with the existing typed builder, then place it
    # at the copied provider path. The bound command must read this source and emit
    # a separate canonical output tree.
    $seedJson = Join-Path $case 'seed.json'
    $seedDds = Join-Path $case 'seed.dds'
    & $dotnet $cli facegen build-tint --game $edition --manifest $manifest --output $seedJson --dds-output $seedDds --json | Out-File -LiteralPath (Join-Path $case 'seed-response.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Seed DDS build failed for $edition." }
    Copy-Item -LiteralPath $seedDds -Destination $source

    $outputTexture = Join-Path $outputRoot ($canonical.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outputTexture) | Out-Null
    $artifact = Join-Path $case 'bound.json'
    $before = Get-TreeFingerprint $data
    & $dotnet $cli facegen build-tint-bound --game $edition --data-root $data --plugins $plugin --npc 0x800 --manifest $manifest --output $artifact --output-root $outputRoot --json | Out-File -LiteralPath (Join-Path $case 'bound-response.json') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Provider-bound FaceTint build failed for $edition." }
    if ((Get-TreeFingerprint $data) -cne $before) { throw "Provider-bound FaceTint build mutated copied Data for $edition." }
    & python $verifier --json (Join-Path $case 'bound-response.json') --edition $edition --data-root $data --output-root $outputRoot --plugin $plugin --canonical $canonical
    if ($LASTEXITCODE -ne 0) { throw "Independent provider-bound FaceTint verifier failed for $edition." }
}

Invoke-BoundFixture 'fallout4' 'P11BoundFO4.esp' 'Textures/Actors/Character/FaceCustomization/P11BoundFO4.esp/00000800_d.dds'
Invoke-BoundFixture 'skyrimse' 'P11BoundSSE.esp' 'Textures/Actors/Character/FaceGenData/FaceTint/P11BoundSSE.esp/00000800.dds'

$negative = Join-Path $root 'negative'
New-Item -ItemType Directory -Force -Path $negative | Out-Null
$validCase = Join-Path $root 'fallout4'
$wrongManifest = Join-Path $negative 'wrong-source.json'
(Get-Content -Raw (Join-Path $validCase 'manifest.json')).Replace('00000800_d.dds', 'wrong.dds') | ForEach-Object {
    [System.IO.File]::WriteAllText($wrongManifest, $_, [System.Text.UTF8Encoding]::new($false))
}
& $dotnet $cli facegen build-tint-bound --game fallout4 --data-root (Join-Path $validCase 'Data') --plugins P11BoundFO4.esp --npc 0x800 --manifest $wrongManifest --output (Join-Path $negative 'wrong.json') --output-root (Join-Path $validCase 'OutputData') --json | Out-File -LiteralPath (Join-Path $negative 'wrong-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Manifest/provider mismatch did not return validation exit 4.' }
$mismatchNpcManifest = Join-Path $negative 'wrong-npc.json'
(Get-Content -Raw (Join-Path $validCase 'manifest.json')).Replace('0x00000800', '0x00000801') | ForEach-Object {
    [System.IO.File]::WriteAllText($mismatchNpcManifest, $_, [System.Text.UTF8Encoding]::new($false))
}
& $dotnet $cli facegen build-tint-bound --game fallout4 --data-root (Join-Path $validCase 'Data') --plugins P11BoundFO4.esp --npc 0x800 --manifest $mismatchNpcManifest --output (Join-Path $negative 'wrong-npc-output.json') --output-root (Join-Path $validCase 'OutputData') --json | Out-File -LiteralPath (Join-Path $negative 'wrong-npc-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Manifest/NPC mismatch did not return validation exit 4.' }
& $dotnet $cli facegen build-tint-bound --game fallout4 --data-root (Join-Path $validCase 'Data') --plugins P11BoundFO4.esp --npc 0x800 --manifest (Join-Path $validCase 'manifest.json') --output (Join-Path $negative 'overlap.json') --output-root (Join-Path $validCase 'Data') --json | Out-File -LiteralPath (Join-Path $negative 'overlap-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 3) { throw 'Source/output root overlap did not return security exit 3.' }
& $dotnet $cli facegen build-tint-bound --game fallout4 --data-root 'F:\ExampleGame\Data' --plugins P11BoundFO4.esp --npc 0x800 --manifest (Join-Path $validCase 'manifest.json') --output (Join-Path $negative 'protected.json') --output-root (Join-Path $validCase 'OutputData') --json | Out-File -LiteralPath (Join-Path $negative 'protected-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 3) { throw 'Protected Data root did not return security exit 3.' }
Write-Output "FACEGEN TINT-BOUND FIXTURES PASS $root"
