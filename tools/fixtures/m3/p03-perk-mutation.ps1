param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_perk_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-perk-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Perk evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3PerksFO4.esp'; $sse = Join-Path $output 'M3PerksSSE.esp'
& $dotnet $generator '--perks' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Perk fixture generation failed.' }

function Invoke-Case([string]$edition, [string]$source, [string[]]$mutation, [string]$expected, [string]$name) {
    $plugin = Split-Path $source -Leaf; $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition',$edition,'--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800') + $mutation + @('--expected-sha256',$hash,'--apply','--json')
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Perk mutation failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x00000800 --plugin $plugin --edition $edition --perks $expected
    if ($LASTEXITCODE -ne 0) { throw "Independent perk verifier failed for $name." }
}

$fo4Name = Split-Path -Leaf $fo4; $sseName = Split-Path -Leaf $sse
Invoke-Case 'fallout4' $fo4 @('--perks',"$fo4Name|0x00000801=0,$fo4Name|0x00000803=255") "$fo4Name|0x00000801=0,$fo4Name|0x00000803=255" 'fallout4-replace'
Invoke-Case 'skyrimse' $sse @('--perks',"$sseName|0x00000801=0,$sseName|0x00000803=255") "$sseName|0x00000801=0,$sseName|0x00000803=255" 'skyrimse-replace'
Invoke-Case 'fallout4' $fo4 @('--update-perk',"$fo4Name|0x00000801=7",'--add-perk',"$fo4Name|0x00000803=128",'--remove-perk',"$fo4Name|0x00000802") "$fo4Name|0x00000801=7,$fo4Name|0x00000803=128" 'fallout4-operations'
Invoke-Case 'skyrimse' $sse @('--update-perk',"$sseName|0x00000801=6",'--add-perk',"$sseName|0x00000803=127",'--remove-perk',"$sseName|0x00000802") "$sseName|0x00000801=6,$sseName|0x00000803=127" 'skyrimse-operations'

$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
$badDuplicate = Join-Path $output 'duplicate.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badDuplicate --form-id 0x800 --add-perk "$fo4Name|0x801=1,$fo4Name|0x801=2" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badDuplicate)) { throw 'Duplicate perk request was not refused before write.' }
$badExternal = Join-Path $output 'external.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badExternal --form-id 0x800 --add-perk 'Other.esp|0x801=1' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badExternal)) { throw 'External perk request was not refused before write.' }
$badRank = Join-Path $output 'rank.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badRank --form-id 0x800 --add-perk "$fo4Name|0x801=256" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badRank)) { throw 'Out-of-range perk rank was not refused at usage boundary.' }
$badConflict = Join-Path $output 'conflict.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badConflict --form-id 0x800 --add-perk "$fo4Name|0x801=1" --remove-perk "$fo4Name|0x801" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badConflict)) { throw 'Conflicting perk operations were not refused before write.' }
Write-Output "P03 PERK FIXTURES PASS $output"
