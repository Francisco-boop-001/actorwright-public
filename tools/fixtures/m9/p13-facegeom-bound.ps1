$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$workspace = (Resolve-Path "$project\..\..").Path
$root = Join-Path $project '03-builds\work\m9-facegeom-bound-evidence'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$fixtureGenerator = Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegeom_bound.py'
$sourceNif = Join-Path $project '01-source-copies\m9-facegen-assets\lumi\femalehead.nif'
$sourceTri = Join-Path $project '01-source-copies\m9-facegen-assets\lumi\femalehead.tri'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Get-TreeFingerprint([string]$path) {
    @(Get-ChildItem -LiteralPath $path -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($path.Length).TrimStart('\', '/').Replace('\', '/')
        "$relative|$($_.Length)|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object) -join "`n"
}

function Invoke-BoundFixture([string]$edition, [string]$plugin) {
    $case = Join-Path $root $edition
    $data = Join-Path $case 'Data'
    $outputRoot = Join-Path $case 'OutputData'
    New-Item -ItemType Directory -Force -Path $case, $data, $outputRoot | Out-Null
    $generated = Join-Path $case $plugin
    & $dotnet $fixtureGenerator '--headparts' $generated (Join-Path $case "$edition-unused.esp")
    if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed for $edition." }
    Copy-Item -LiteralPath $generated -Destination (Join-Path $data $plugin)

    $canonical = "Meshes/Actors/Character/FaceGenData/FaceGeom/$plugin/00000800.nif"
    $source = Join-Path $data ($canonical.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $source) | Out-Null
    Copy-Item -LiteralPath $sourceNif -Destination $source
    Copy-Item -LiteralPath $sourceTri -Destination ([IO.Path]::ChangeExtension($source, '.tri'))
    $output = Join-Path $outputRoot ($canonical.Replace('/', '\'))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $output) | Out-Null
    $morphs = Join-Path $case 'morphs.json'
    [IO.File]::WriteAllText($morphs, '[{"name":"BrowDownLeft","value":0.35}]', [Text.UTF8Encoding]::new($false))
    $response = Join-Path $case 'bound-response.json'
    $before = Get-TreeFingerprint $data
    & $dotnet $cli facegen build-geom-bound --game $edition --data-root $data --plugins $plugin --npc 0x800 `
        --output-root $outputRoot --morphs "@$morphs" --json | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Provider-bound FaceGeom build failed for $edition." }
    if ((Get-TreeFingerprint $data) -cne $before) { throw "Provider-bound FaceGeom build mutated copied Data for $edition." }
    & python $verifier --json $response --edition $edition --data-root $data --output-root $outputRoot --plugin $plugin --canonical $canonical
    if ($LASTEXITCODE -ne 0) { throw "Independent provider-bound FaceGeom verifier failed for $edition." }
}

Invoke-BoundFixture 'fallout4' 'P13BoundFO4.esp'
Invoke-BoundFixture 'skyrimse' 'P13BoundSSE.esp'

$negative = Join-Path $root 'negative'
New-Item -ItemType Directory -Force -Path $negative | Out-Null
$validCase = Join-Path $root 'fallout4'
$morphs = Join-Path $validCase 'morphs.json'
& $dotnet $cli facegen build-geom-bound --game fallout4 --data-root (Join-Path $validCase 'Data') --plugins P13BoundFO4.esp --npc 0x800 `
    --output-root (Join-Path $validCase 'Data') --morphs "@$morphs" --json | Out-File -LiteralPath (Join-Path $negative 'overlap-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 3) { throw 'Source/output root overlap did not return security exit 3.' }
& $dotnet $cli facegen build-geom-bound --game fallout4 --data-root 'F:\ExampleGame\Data' --plugins P13BoundFO4.esp --npc 0x800 `
    --output-root (Join-Path $validCase 'OutputData') --morphs "@$morphs" --json | Out-File -LiteralPath (Join-Path $negative 'protected-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 3) { throw 'Protected Data root did not return security exit 3.' }
Write-Output "FACEGEN GEOM-BOUND FIXTURES PASS $root"
