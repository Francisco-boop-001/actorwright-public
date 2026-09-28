param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_body_sidecar.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p11-body-sidecar-write-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Sidecar evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

$fixtures = @(
    @{ Edition = 'fallout4'; Plugin = 'P11FixtureFO4.esp'; Output = 'P11FixtureFO4.bssliders'; Evidence = 'fallout4-inspect.json'; Morphs = '{"Calf":0.25,"Waist":-0.1}' },
    @{ Edition = 'skyrimse'; Plugin = 'P11FixtureSSE.esp'; Output = 'P11FixtureSSE.bssliders'; Evidence = 'skyrimse-inspect.json'; Morphs = '{"Breast":0.4}' }
)
foreach ($fixture in $fixtures) {
    $sidecar = Join-Path $output $fixture.Output
    $morphsPath = Join-Path $output "$($fixture.Edition)-morphs.json"
    [IO.File]::WriteAllText($morphsPath, $fixture.Morphs, (New-Object Text.UTF8Encoding($false)))
    $writeEvidence = Join-Path $output "$($fixture.Edition)-write.json"
    & $dotnet $cli body sidecar write --game $fixture.Edition --plugin $fixture.Plugin --npc 0x000800 --sliders "@$morphsPath" --output $sidecar --json | Out-File -LiteralPath $writeEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Sidecar write failed for $($fixture.Edition)." }
    $write = Get-Content -LiteralPath $writeEvidence -Raw | ConvertFrom-Json
    if (-not $write.written) { throw "Sidecar writer did not report written=true for $($fixture.Edition)." }
    $inspectEvidence = Join-Path $output $fixture.Evidence
    & $dotnet $cli body sidecar inspect --game $fixture.Edition --file $sidecar --json | Out-File -LiteralPath $inspectEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Sidecar reload failed for $($fixture.Edition)." }
    & $python $verifier --json $inspectEvidence --expect allowed
    if ($LASTEXITCODE -ne 0) { throw "Independent sidecar verifier failed for $($fixture.Edition)." }
}

$unsafe = Join-Path $output 'unsafe.bssliders'
$unsafeMorphs = Join-Path $output 'unsafe-morphs.json'
[IO.File]::WriteAllText($unsafeMorphs, '{"Calf":0.1}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli body sidecar write --game fallout4 --plugin P11Unsafe.esp --npc 0x000800 --sliders "@$unsafeMorphs" --output 'F:\ExampleGame\Data\unsafe.bssliders' --json 2>$null | Out-File -LiteralPath (Join-Path $output 'unsafe.json') -Encoding utf8
if ($LASTEXITCODE -ne 3 -or (Test-Path -LiteralPath $unsafe)) { throw 'Outside-K sidecar output was not refused.' }
Write-Output "P11 BODY SIDECAR WRITE FIXTURES PASS $output"
