param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_section_reset.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-section-reset-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Reset evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
$baselineRoot = Join-Path $output 'baseline'; $currentRoot = Join-Path $output 'current'; $resultRoot = Join-Path $output 'result'
New-Item -ItemType Directory -Force -Path $baselineRoot,$currentRoot,$resultRoot | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
& $dotnet build (Join-Path $projectRoot 'src\NpcManager.Cli\NpcManager.Cli.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }

function Invoke-Case([string]$game, [string]$suffix) {
    $name = "M3Reset$suffix.esp"; $baseline = Join-Path $baselineRoot $name; $current = Join-Path $currentRoot $name
    $edited = Join-Path $currentRoot "edited-$name"; $result = Join-Path $resultRoot $name; $evidence = Join-Path $resultRoot "$suffix.json"
    if ($game -eq 'fallout4') {
        & $dotnet $generator '--factions' $baseline (Join-Path $baselineRoot "unused-$suffix.esp")
    } else {
        & $dotnet $generator '--factions' (Join-Path $baselineRoot "unused-$suffix.esp") $baseline
    }
    Copy-Item -LiteralPath $baseline -Destination $current
    $hash = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash.ToLowerInvariant()
    $faction = "$name|0x00000801=77"
    & $dotnet $cli npc patch --game $game --input-plugin $current --output $edited --form-id 0x800 --factions $faction --expected-sha256 $hash --apply --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Mutation setup failed for $suffix." }
    Remove-Item -LiteralPath $current -Force; Move-Item -LiteralPath $edited -Destination $current
    $editedHash = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli npc reset --game $game --plugin $current --baseline $baseline --output $result --npc 0x800 --section factions --expected-sha256 $editedHash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Reset failed for $suffix." }
    & $python $verifier --current $current --baseline $baseline --output $result --json $evidence --form-id 0x800 --field factions
    if ($LASTEXITCODE -ne 0) { throw "Independent reset verifier failed for $suffix." }
}

Invoke-Case 'fallout4' 'FO4'
Invoke-Case 'skyrimse' 'SSE'

$bad = Join-Path $resultRoot 'bad-name.esp'; $hash = (Get-FileHash -LiteralPath (Join-Path $currentRoot 'M3ResetFO4.esp') -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc reset --game fallout4 --plugin (Join-Path $currentRoot 'M3ResetFO4.esp') --baseline (Join-Path $baselineRoot 'M3ResetSSE.esp') --output $bad --npc 0x800 --section factions --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $bad)) { throw 'Cross-plugin baseline was not refused.' }

$noop = Join-Path $resultRoot 'noop.esp'; $source = Join-Path $baselineRoot 'M3ResetFO4.esp'; $noopBaselineRoot = Join-Path $baselineRoot 'noop'; New-Item -ItemType Directory -Force -Path $noopBaselineRoot | Out-Null
$noopBaseline = Join-Path $noopBaselineRoot 'M3ResetFO4.esp'; Copy-Item -LiteralPath $source -Destination $noopBaseline
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc reset --game fallout4 --plugin $source --baseline $noopBaseline --output $noop --npc 0x800 --section factions --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $noop)) { throw 'No-op reset did not remain non-writing.' }

Write-Output "P03 RESET FIXTURES PASS $output"
