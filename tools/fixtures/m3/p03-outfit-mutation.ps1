param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_outfit_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-outfit-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Outfit evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3OutfitsFO4.esp'; $sse = Join-Path $output 'M3OutfitsSSE.esp'
& $dotnet $generator '--outfits' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Outfit fixture generation failed.' }

function Invoke-Case([string]$edition, [string]$source, [string[]]$mutation, [string]$expectedDefault, [string]$expectedSleep, [string]$name) {
    $plugin = Split-Path $source -Leaf; $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition',$edition,'--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800') + $mutation + @('--expected-sha256',$hash,'--apply','--json')
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Outfit mutation failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x00000800 --plugin $plugin --default $expectedDefault --sleep $expectedSleep
    if ($LASTEXITCODE -ne 0) { throw "Independent outfit verifier failed for $name." }
}

$fo4Name = Split-Path -Leaf $fo4; $sseName = Split-Path -Leaf $sse
Invoke-Case 'fallout4' $fo4 @('--default-outfit',"$fo4Name|0x00000803",'--sleep-outfit','none') "$fo4Name|0x00000803" 'none' 'fallout4-replace'
Invoke-Case 'skyrimse' $sse @('--default-outfit',"$sseName|0x00000803",'--sleep-outfit','none') "$sseName|0x00000803" 'none' 'skyrimse-replace'
Invoke-Case 'fallout4' $fo4 @('--default-outfit','none','--sleep-outfit','none') 'none' 'none' 'fallout4-clear'
Invoke-Case 'skyrimse' $sse @('--default-outfit','none','--sleep-outfit','none') 'none' 'none' 'skyrimse-clear'

$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
$badNoOp = Join-Path $output 'no-op.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badNoOp --form-id 0x800 --default-outfit "$fo4Name|0x801" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badNoOp)) { throw 'No-op outfit request was not refused before write.' }
$badExternal = Join-Path $output 'external.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badExternal --form-id 0x800 --default-outfit 'Other.esp|0x801' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badExternal)) { throw 'External outfit request was not refused before write.' }
$badMalformed = Join-Path $output 'malformed.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badMalformed --form-id 0x800 --default-outfit 'not-a-form-id' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badMalformed)) { throw 'Malformed outfit request was not refused at usage boundary.' }
$badStale = Join-Path $output 'stale.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badStale --form-id 0x800 --default-outfit "$fo4Name|0x803" --expected-sha256 ('0' * 64) --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badStale)) { throw 'Stale outfit hash was not refused before write.' }
Write-Output "P03 OUTFIT FIXTURES PASS $output"
