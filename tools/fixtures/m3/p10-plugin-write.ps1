param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_plugin_write.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p10-plugin-write-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Plugin-write evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

$fixtures = @(
    @{ Edition = 'fallout4'; Game = 'fo4'; Plugin = 'M2FixtureFO4.esp'; Output = 'M3PluginWriteFO4.esp'; Evidence = 'fallout4.json' },
    @{ Edition = 'skyrimse'; Game = 'sse'; Plugin = 'M2FixtureSSE.esp'; Output = 'M3PluginWriteSSE.esp'; Evidence = 'skyrimse.json' }
)
foreach ($fixture in $fixtures) {
    $source = (Resolve-Path (Join-Path $projectRoot "01-source-copies\m2-fixtures\$($fixture.Game)\Data\$($fixture.Plugin)")).Path
    $outputPlugin = Join-Path $output $fixture.Output
    $proposal = Join-Path $output "$($fixture.Edition).npc-proposal.json"
    $evidence = Join-Path $output $fixture.Evidence
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ schemaVersion = 1; edition = $fixture.Edition; inputPlugin = $source; outputPlugin = $outputPlugin; targetFormId = '0x00000800'; inputSha256 = $hash; changes = @([ordered]@{ field = 'Sex'; before = 'male'; after = 'female' }); preservedFields = @('ACBS', 'DATA') } |
        ConvertTo-Json -Depth 10 | Out-File -LiteralPath $proposal -Encoding utf8
    & $dotnet $cli plugin write --game $fixture.Edition --proposal $proposal --output $outputPlugin --no-overwrite --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Plugin write failed for $($fixture.Edition)." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --expect allowed
    if ($LASTEXITCODE -ne 0) { throw "Independent plugin-write verifier failed for $($fixture.Edition)." }

    $staleOutput = Join-Path $output "$($fixture.Edition)-stale.esp"
    [ordered]@{ schemaVersion = 1; edition = $fixture.Edition; inputPlugin = $source; outputPlugin = $staleOutput; targetFormId = '0x00000800'; inputSha256 = ('0' * 64); changes = @([ordered]@{ field = 'Sex'; before = 'male'; after = 'female' }); preservedFields = @('ACBS') } |
        ConvertTo-Json -Depth 10 | Out-File -LiteralPath $proposal -Encoding utf8
    $staleEvidence = Join-Path $output "$($fixture.Edition)-stale.json"
    & $dotnet $cli plugin write --game $fixture.Edition --proposal $proposal --output $staleOutput --no-overwrite --json | Out-File -LiteralPath $staleEvidence -Encoding utf8
    if ($LASTEXITCODE -eq 0) { throw "Stale plugin write unexpectedly succeeded for $($fixture.Edition)." }
    & $python $verifier --source $source --output $staleOutput --json $staleEvidence --expect refused
    if ($LASTEXITCODE -ne 0) { throw "Stale plugin-write refusal verifier failed for $($fixture.Edition)." }
}
Write-Output "P10 PLUGIN WRITE FIXTURES PASS $output"
