param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_generated_artifacts.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\p01-generated-scan-evidence'
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$projectPrefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Generated-scan evidence must remain under the project root.'
}

function UInt16-Le([int]$value) { return [BitConverter]::GetBytes([uint16]$value) }
function UInt32-Le([uint64]$value) { return [BitConverter]::GetBytes([uint32]$value) }
function Subrecord([string]$signature, [byte[]]$payload) {
    return [byte[]](([Text.Encoding]::ASCII.GetBytes($signature)) + (UInt16-Le $payload.Length) + $payload)
}
function MarkerPlugin() {
    $hedrData = [byte[]](([BitConverter]::GetBytes([single]1.0)) + (UInt32-Le 0) + (UInt32-Le 0x800))
    $marker = [byte[]](([Text.Encoding]::ASCII.GetBytes('NPC Manager')) + [byte]0)
    $body = [byte[]]((Subrecord 'HEDR' $hedrData) + (Subrecord 'CNAM' $marker))
    $header = [byte[]](([Text.Encoding]::ASCII.GetBytes('TES4')) + (UInt32-Le $body.Length) +
        (UInt32-Le 0) + (UInt32-Le 0) + (UInt32-Le 0) + (UInt16-Le 1) + (UInt16-Le 0))
    return [byte[]]($header + $body)
}
function WriteBytes([string]$path, [byte[]]$bytes) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllBytes($path, $bytes)
}
function WriteFixture([string]$data, [string]$edition, [string]$plugin) {
    WriteBytes (Join-Path $data $plugin) (MarkerPlugin)
    $faceGeom = Join-Path $data "Meshes\Actors\Character\FaceGenData\FaceGeom\$plugin"
    WriteBytes (Join-Path $faceGeom '00000800.nif') ([byte[]](1, 2, 3))
    WriteBytes (Join-Path $faceGeom '00000800_2.nif') ([byte[]](4, 5, 6))
    if ($edition -eq 'fallout4') {
        $customization = Join-Path $data "Textures\Actors\Character\FaceCustomization\$plugin"
        WriteBytes (Join-Path $customization '00000800_d.dds') ([byte[]](7))
        WriteBytes (Join-Path $customization '00000800_msn.dds') ([byte[]](8))
        WriteBytes (Join-Path $customization '00000800_s.dds') ([byte[]](9))
    } else {
        $tint = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceTint\$plugin"
        WriteBytes (Join-Path $tint '00000800.dds') ([byte[]](7))
        WriteBytes (Join-Path $tint 'facedetailneutral.dds') ([byte[]](8))
        $diffuse = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceDiffuse\$plugin"
        WriteBytes (Join-Path $diffuse '00000800.dds') ([byte[]](9))
        $normal = Join-Path $data "Textures\Actors\Character\FaceGenData\FaceNormal\$plugin"
        WriteBytes (Join-Path $normal '00000800.dds') ([byte[]](10))
    }
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source
$fixtures = @(
    @{ Edition = 'fallout4'; Plugin = 'GeneratedFixtureFO4.esp' },
    @{ Edition = 'skyrimse'; Plugin = 'GeneratedFixtureSSE.esp' }
)
foreach ($fixture in $fixtures) {
    $data = Join-Path $output "$($fixture.Edition)\Data"
    WriteFixture $data $fixture.Edition $fixture.Plugin
    $json = Join-Path $output "$($fixture.Edition)-allowed.json"
    & $dotnet $cli workspace scan-generated --game $fixture.Edition --data-root $data --json |
        Out-File -LiteralPath $json -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Generated scan failed for $($fixture.Edition)." }
    & $python $verifier --json $json --expect allowed
    if ($LASTEXITCODE -ne 0) { throw "Independent verifier rejected $($fixture.Edition) allowed evidence." }
}

$allowed = Join-Path $output 'fallout4-allowed.json'
$refused = Join-Path $output 'fallout4-refused.json'
$tampered = (Get-Content -LiteralPath $allowed -Raw) -replace '"isValid"\s*:\s*true', '"isValid": false'
$tampered = $tampered -replace '"diagnostics"\s*:\s*\[\]', '"diagnostics": [{"code":"fixture-refusal","severity":"error","message":"intentional verifier refusal"}]'
[IO.File]::WriteAllText($refused, $tampered, (New-Object Text.UTF8Encoding($false)))
& $python $verifier --json $refused --expect refused --reason fixture-refusal
if ($LASTEXITCODE -ne 0) { throw 'Independent verifier did not accept the intentional refusal evidence.' }

Write-Output "GENERATED SCAN FIXTURES PASS $output"
