param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_plugin_verify.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p10-plugin-verify-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Plugin-verify evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

$fixtures = @(
    @{ Edition = 'fallout4'; Game = 'fo4'; Plugin = 'M2FixtureFO4.esp'; Output = 'M3PluginVerifyFO4.esp'; Proposal = 'fallout4.npc-proposal.json'; Evidence = 'fallout4.json' },
    @{ Edition = 'skyrimse'; Game = 'sse'; Plugin = 'M2FixtureSSE.esp'; Output = 'M3PluginVerifySSE.esp'; Proposal = 'skyrimse.npc-proposal.json'; Evidence = 'skyrimse.json' }
)
function Find-AcbsPayloadOffset([byte[]]$bytes) {
    for ($i = 0; $i -le $bytes.Length - 10; $i++) {
        if ($bytes[$i] -eq [byte][char]'A' -and $bytes[$i + 1] -eq [byte][char]'C' -and $bytes[$i + 2] -eq [byte][char]'B' -and $bytes[$i + 3] -eq [byte][char]'S') {
            $size = [BitConverter]::ToUInt16($bytes, $i + 4)
            if ($size -ge 4 -and $i + 6 + $size -le $bytes.Length) { return $i + 6 }
        }
    }
    throw 'ACBS not found.'
}
foreach ($fixture in $fixtures) {
    $source = (Resolve-Path (Join-Path $projectRoot "01-source-copies\m2-fixtures\$($fixture.Game)\Data\$($fixture.Plugin)")).Path
    $target = Join-Path $output $fixture.Output
    $proposal = Join-Path $output $fixture.Proposal
    $patchEvidence = Join-Path $output "$($fixture.Edition)-patch.json"
    $verifyEvidence = Join-Path $output $fixture.Evidence
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli npc patch --game $fixture.Edition --input-plugin $source --output $target --form-id 0x00000800 --sex female --expected-sha256 $hash --proposal $proposal --apply --json | Out-File -LiteralPath $patchEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Patch fixture failed for $($fixture.Edition)." }
    & $dotnet $cli plugin verify --game $fixture.Edition --before $source --after $target --proposal $proposal --json | Out-File -LiteralPath $verifyEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Plugin verify failed for $($fixture.Edition)." }
    & $python $verifier --json $verifyEvidence --expect allowed
    if ($LASTEXITCODE -ne 0) { throw "Independent plugin verifier failed for $($fixture.Edition)." }

    $drifted = Join-Path $output "$($fixture.Edition)-drift.esp"
    $bytes = [IO.File]::ReadAllBytes($target)
    $offset = Find-AcbsPayloadOffset $bytes
    [BitConverter]::GetBytes([uint32]([BitConverter]::ToUInt32($bytes, $offset) -bor 4)).CopyTo($bytes, $offset)
    [IO.File]::WriteAllBytes($drifted, $bytes)
    $driftEvidence = Join-Path $output "$($fixture.Edition)-drift.json"
    & $dotnet $cli plugin verify --game $fixture.Edition --before $source --after $drifted --proposal $proposal --json | Out-File -LiteralPath $driftEvidence -Encoding utf8
    if ($LASTEXITCODE -eq 0) { throw "Drifted plugin unexpectedly passed for $($fixture.Edition)." }
    & $python $verifier --json $driftEvidence --expect refused
    if ($LASTEXITCODE -ne 0) { throw "Drift refusal verifier failed for $($fixture.Edition)." }
}
Write-Output "P10 PLUGIN VERIFY FIXTURES PASS $output"
