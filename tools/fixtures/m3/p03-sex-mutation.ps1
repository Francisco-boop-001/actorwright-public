param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_sex_mutation.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\p03-sex-mutation-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }

$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Sex-mutation evidence must remain under the project root.'
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

function Find-AcbsPayloadOffset([byte[]]$bytes) {
    for ($i = 0; $i -le $bytes.Length - 10; $i++) {
        if ($bytes[$i] -eq [byte][char]'A' -and $bytes[$i + 1] -eq [byte][char]'C' -and
            $bytes[$i + 2] -eq [byte][char]'B' -and $bytes[$i + 3] -eq [byte][char]'S') {
            $size = [BitConverter]::ToUInt16($bytes, $i + 4)
            if ($size -ge 4 -and $i + 6 + $size -le $bytes.Length) { return $i + 6 }
        }
    }
    throw 'Fixture output did not contain a valid ACBS subrecord.'
}

function Invoke-Verifier([string]$source, [string]$outputPlugin, [string]$evidence, [string]$expected, [string]$expect, [string]$reason = '') {
    $arguments = @($verifier, '--source', $source, '--output', $outputPlugin, '--json', $evidence,
        '--form-id', '0x00000800', '--expected-sex', $expected, '--expect', $expect)
    if (-not [string]::IsNullOrWhiteSpace($reason)) { $arguments += @('--reason', $reason) }
    & $python @arguments
    if ($LASTEXITCODE -ne 0) { throw "Independent sex verifier failed for $expect ($expected)." }
}

$fixtures = @(
    @{ Edition = 'fallout4'; Game = 'fo4'; Plugin = 'M2FixtureFO4.esp'; Output = 'M3SexFO4.esp'; Evidence = 'fallout4.json' },
    @{ Edition = 'skyrimse'; Game = 'sse'; Plugin = 'M2FixtureSSE.esp'; Output = 'M3SexSSE.esp'; Evidence = 'skyrimse.json' }
)

foreach ($fixture in $fixtures) {
    $source = (Resolve-Path (Join-Path $projectRoot "01-source-copies\m2-fixtures\$($fixture.Game)\Data\$($fixture.Plugin)")).Path
    $outputPlugin = Join-Path $output $fixture.Output
    $evidence = Join-Path $output $fixture.Evidence
    $expectedHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()

    & $dotnet $cli npc patch --game $fixture.Edition --input-plugin $source --output $outputPlugin --form-id 0x00000800 --sex female --expected-sha256 $expectedHash --apply --json |
        Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Typed sex mutation failed for $($fixture.Edition)." }
    Invoke-Verifier $source $outputPlugin $evidence 'female' 'allowed'

    $drifted = Join-Path $output "$([IO.Path]::GetFileNameWithoutExtension($fixture.Output))-drift.esp"
    $bytes = [IO.File]::ReadAllBytes($outputPlugin)
    $acbsPayload = Find-AcbsPayloadOffset $bytes
    $flags = [BitConverter]::ToUInt32($bytes, $acbsPayload)
    [BitConverter]::GetBytes([uint32]($flags -bor 2)).CopyTo($bytes, $acbsPayload)
    [IO.File]::WriteAllBytes($drifted, $bytes)
    $driftEvidence = Join-Path $output "$($fixture.Edition)-drift.json"
    $driftJson = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
    $driftJson.outputSha256 = (Get-FileHash -LiteralPath $drifted -Algorithm SHA256).Hash.ToLowerInvariant()
    $driftJson | ConvertTo-Json -Depth 20 | Out-File -LiteralPath $driftEvidence -Encoding utf8
    Invoke-Verifier $source $drifted $driftEvidence 'female' 'refused' 'non-female-acbs-bit-drift'
}

Write-Output "P03 SEX FIXTURES PASS $output"
