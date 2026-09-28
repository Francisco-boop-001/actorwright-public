param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_bodyslide_tri.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\m4-bodyslide-tri-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'BodySlide TRI evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Ascii([IO.BinaryWriter]$writer, [string]$value) {
    $bytes = [Text.Encoding]::ASCII.GetBytes($value)
    $writer.Write([byte]$bytes.Length)
    $writer.Write($bytes)
}

function Write-PositionMorph([IO.BinaryWriter]$writer, [string]$name, [float]$multiplier,
    [UInt16]$vertex, [Int16]$x, [Int16]$y, [Int16]$z) {
    Write-Ascii $writer $name
    $writer.Write($multiplier)
    $writer.Write([UInt16]1)
    $writer.Write($vertex)
    $writer.Write($x); $writer.Write($y); $writer.Write($z)
}

function Write-UvMorph([IO.BinaryWriter]$writer, [string]$name, [float]$multiplier,
    [UInt16]$vertex, [Int16]$x, [Int16]$y) {
    Write-Ascii $writer $name
    $writer.Write($multiplier)
    $writer.Write([UInt16]1)
    $writer.Write($vertex)
    $writer.Write($x); $writer.Write($y)
}

$triPath = Join-Path $output 'BodySlideFixture.tri'
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter($stream, [Text.Encoding]::ASCII, $true)
$writer.Write([Text.Encoding]::ASCII.GetBytes('PIRT'))
$writer.Write([UInt16]1)
Write-Ascii $writer 'BaseFemaleBody'
$writer.Write([UInt16]2)
Write-PositionMorph $writer 'BigBelly' 1.0 3 1 2 3
Write-PositionMorph $writer 'WeightThin' 1.0 4 1 0 0
$writer.Write([UInt16]1)
Write-Ascii $writer 'BaseFemaleBody'
$writer.Write([UInt16]1)
Write-UvMorph $writer 'BigBelly' 1.0 3 9 9
$writer.Flush()
[IO.File]::WriteAllBytes($triPath, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()

function Write-Json([string]$path, [string]$json) {
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

$fo4Preset = Join-Path $output 'fo4-looksmenu.json'
Write-Json $fo4Preset '{"BodyMorphs":{"BigBelly":0.5,"Missing":0.25,"WeightThin":0.5}}'
$ssePreset = Join-Path $output 'sse-racemenu.jslot'
Write-Json $ssePreset '{"bodyMorphs":[{"name":"BigBelly","keys":[{"key":"NPCManager","value":0.4}]},{"name":"Missing","keys":[{"key":"NPCManager","value":0.2}]},{"name":"WeightThin","keys":[{"key":"NPCManager","value":0.5}]}]}'

foreach ($fixture in @(
    @{ Edition = 'fallout4'; Preset = $fo4Preset; Name = 'fo4' },
    @{ Edition = 'skyrimse'; Preset = $ssePreset; Name = 'sse' }
)) {
    $response = Join-Path $output "$($fixture.Name)-resolve.json"
    & $dotnet $cli body sliders resolve --game $fixture.Edition --tri $triPath --preset $fixture.Preset --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "BodySlide TRI resolution failed for $($fixture.Edition)." }
    python $verifier --tri $triPath --preset $fixture.Preset --response $response
    if ($LASTEXITCODE -ne 0) { throw "Independent BodySlide TRI verification failed for $($fixture.Edition)." }
}

$malformed = Join-Path $output 'malformed.tri'
[IO.File]::WriteAllBytes($malformed, [Text.Encoding]::ASCII.GetBytes('PIRT'))
$malformedResponse = Join-Path $output 'malformed-refused.json'
& $dotnet $cli body sliders resolve --game fallout4 --tri $malformed --preset $fo4Preset --json |
    Out-File -LiteralPath $malformedResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Malformed PIRT did not return validation failure.' }

python -m py_compile $verifier
Write-Output "BODYSLIDE TRI FIXTURES PASS $output"
