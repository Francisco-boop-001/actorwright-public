param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = (Resolve-Path (Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_stats_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-stats-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Stats evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }

$fo4 = Join-Path $output 'M3StatsFO4.esp'; $sse = Join-Path $output 'M3StatsSSE.esp'
& $dotnet $generator '--stats' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Stats fixture generation failed.' }

function Run-Allowed([string]$edition, [string]$source, [string[]]$options, [string[]]$expected) {
    $name = if ($edition -eq 'fallout4') { 'M3StatsFO4' } else { 'M3StatsSSE' }
    $outputPlugin = Join-Path $output "$name-mutated.esp"; $evidence = Join-Path $output "$edition-allowed.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli, 'npc', 'patch', '--edition', $edition, '--input-plugin', $source, '--output', $outputPlugin,
        '--form-id', '0x00000800', '--expected-sha256', $hash, '--apply', '--json') + $options
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Stats mutation failed for $edition." }
    $verifyArgs = @($verifier, '--source', $source, '--output', $outputPlugin, '--json', $evidence,
        '--edition', $edition, '--form-id', '0x00000800')
    foreach ($item in $expected) { $verifyArgs += @('--expected', $item) }
    & $python @verifyArgs
    if ($LASTEXITCODE -ne 0) { throw "Independent stats verifier failed for $edition." }
}

Run-Allowed 'fallout4' $fo4 @('--level','22','--xp-offset','-4','--calc-min','5','--calc-max','50','--disposition','8','--bleedout','9','--set-flag','essential','--clear-flag','unique') @('Level=22','XpValueOffset=-4','CalcMinLevel=5','CalcMaxLevel=50','DispositionBase=8','BleedoutOverride=9','Flag:essential=true','Flag:unique=false')
Run-Allowed 'skyrimse' $sse @('--level-mult','1.234','--magicka-offset','-4','--stamina-offset','6','--health-offset','8','--calc-min','4','--calc-max','60','--speed-multiplier','120','--disposition','10','--bleedout','11','--height','1.05','--player-health','110','--player-magicka','90','--player-stamina','100','--skill-values','one-handed=35,archery=40','--skill-offsets','one-handed=2,archery=3','--set-flag','essential','--clear-flag','unique') @('Level=1.234','Flag:pc-level-mult=true','MagickaOffset=-4','StaminaOffset=6','HealthOffset=8','CalcMinLevel=4','CalcMaxLevel=60','SpeedMultiplier=120','DispositionBase=10','BleedoutOverride=11','Height=1.05','PlayerHealth=110','PlayerMagicka=90','PlayerStamina=100','SkillValue:onehanded=35','SkillValue:archery=40','SkillOffset:onehanded=2','SkillOffset:archery=3','Flag:essential=true','Flag:unique=false')

$refused = Join-Path $output 'fo4-height-refused.esp'
$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $refused --form-id 0x00000800 --height 1.1 --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $refused)) { throw 'FO4 Skyrim-only height was not refused.' }
Write-Output "P03 STATS FIXTURES PASS $output"
