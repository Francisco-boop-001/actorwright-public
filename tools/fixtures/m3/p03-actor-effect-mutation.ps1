param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_actor_effect_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-actor-effect-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Actor-effect evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3ActorEffectsFO4.esp'; $sse = Join-Path $output 'M3ActorEffectsSSE.esp'
& $dotnet $generator '--actor-effects' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Actor-effect fixture generation failed.' }

function Invoke-Case([string]$edition, [string]$source, [string[]]$mutation, [string]$expected, [string]$name) {
    $plugin = Split-Path $source -Leaf; $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition',$edition,'--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800') + $mutation + @('--expected-sha256',$hash,'--apply','--json')
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Actor-effect mutation failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x00000800 --plugin $plugin --actor-effects $expected
    if ($LASTEXITCODE -ne 0) { throw "Independent actor-effect verifier failed for $name." }
}

$fo4Name = Split-Path -Leaf $fo4; $sseName = Split-Path -Leaf $sse
Invoke-Case 'fallout4' $fo4 @('--actor-effects',"$fo4Name|0x00000801,$fo4Name|0x00000803") "$fo4Name|0x00000801,$fo4Name|0x00000803" 'fallout4-replace'
Invoke-Case 'skyrimse' $sse @('--actor-effects',"$sseName|0x00000801,$sseName|0x00000803") "$sseName|0x00000801,$sseName|0x00000803" 'skyrimse-replace'
Invoke-Case 'fallout4' $fo4 @('--add-actor-effect',"$fo4Name|0x00000803",'--remove-actor-effect',"$fo4Name|0x00000802") "$fo4Name|0x00000801,$fo4Name|0x00000803" 'fallout4-operations'
Invoke-Case 'skyrimse' $sse @('--add-actor-effect',"$sseName|0x00000803",'--remove-actor-effect',"$sseName|0x00000802") "$sseName|0x00000801,$sseName|0x00000803" 'skyrimse-operations'

$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
$badDuplicate = Join-Path $output 'duplicate.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badDuplicate --form-id 0x800 --add-actor-effect "$fo4Name|0x801,$fo4Name|0x801" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badDuplicate)) { throw 'Duplicate actor-effect request was not refused at usage boundary.' }
$badExternal = Join-Path $output 'external.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badExternal --form-id 0x800 --add-actor-effect 'Other.esp|0x801' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badExternal)) { throw 'External actor-effect request was not refused before write.' }
$badConflict = Join-Path $output 'conflict.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badConflict --form-id 0x800 --add-actor-effect "$fo4Name|0x801" --remove-actor-effect "$fo4Name|0x801" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badConflict)) { throw 'Conflicting actor-effect operations were not refused before write.' }
$badMalformed = Join-Path $output 'malformed.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badMalformed --form-id 0x800 --add-actor-effect 'not-a-form-id' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badMalformed)) { throw 'Malformed actor-effect request was not refused at usage boundary.' }
Write-Output "P03 ACTOR-EFFECT FIXTURES PASS $output"
