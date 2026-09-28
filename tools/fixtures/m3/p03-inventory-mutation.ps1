param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_inventory_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-inventory-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Inventory evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3InventoryFO4.esp'; $sse = Join-Path $output 'M3InventorySSE.esp'
& $dotnet $generator '--inventory' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Inventory fixture generation failed.' }

function Invoke-Case([string]$edition, [string]$source, [string[]]$mutation, [string]$expected, [string]$name) {
    $plugin = Split-Path $source -Leaf; $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition',$edition,'--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800') + $mutation + @('--expected-sha256',$hash,'--apply','--json')
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Inventory mutation failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x00000800 --plugin $plugin --inventory $expected
    if ($LASTEXITCODE -ne 0) { throw "Independent inventory verifier failed for $name." }
}

$fo4Name = Split-Path -Leaf $fo4; $sseName = Split-Path -Leaf $sse
Invoke-Case 'fallout4' $fo4 @('--inventory',"$fo4Name|0x00000801=5,$fo4Name|0x00000803=-128") "$fo4Name|0x00000801=5,$fo4Name|0x00000803=-128" 'fallout4-replace'
Invoke-Case 'skyrimse' $sse @('--inventory',"$sseName|0x00000801=5,$sseName|0x00000803=-128") "$sseName|0x00000801=5,$sseName|0x00000803=-128" 'skyrimse-replace'
Invoke-Case 'fallout4' $fo4 @('--update-inventory',"$fo4Name|0x00000801=7",'--add-inventory',"$fo4Name|0x00000803=127",'--remove-inventory',"$fo4Name|0x00000802") "$fo4Name|0x00000801=7,$fo4Name|0x00000803=127" 'fallout4-operations'
Invoke-Case 'skyrimse' $sse @('--update-inventory',"$sseName|0x00000801=-128",'--add-inventory',"$sseName|0x00000803=126",'--remove-inventory',"$sseName|0x00000802") "$sseName|0x00000801=-128,$sseName|0x00000803=126" 'skyrimse-operations'
Invoke-Case 'fallout4' $fo4 @('--add-inventory',"$fo4Name|0x00000803=2,$fo4Name|0x00000803=3") "$fo4Name|0x00000801=2,$fo4Name|0x00000802=-1,$fo4Name|0x00000803=5" 'fallout4-duplicate-add-merge'
Invoke-Case 'skyrimse' $sse @('--inventory',"$sseName|0x00000803=2,$sseName|0x00000803=3") "$sseName|0x00000803=5" 'skyrimse-duplicate-replace-merge'

$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
$badDuplicate = Join-Path $output 'duplicate-update.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badDuplicate --form-id 0x800 --update-inventory "$fo4Name|0x801=1,$fo4Name|0x801=2" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badDuplicate)) { throw 'Duplicate inventory update was not refused before write.' }
$badExternal = Join-Path $output 'external.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badExternal --form-id 0x800 --add-inventory 'Other.esp|0x801=1' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badExternal)) { throw 'External inventory request was not refused before write.' }
$badCount = Join-Path $output 'count-range.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badCount --form-id 0x800 --add-inventory "$fo4Name|0x803=2147483648" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badCount)) { throw 'Out-of-range inventory count was not refused at usage boundary.' }
$badOverflow = Join-Path $output 'count-overflow.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badOverflow --form-id 0x800 --add-inventory "$fo4Name|0x803=2147483647,$fo4Name|0x803=1" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badOverflow)) { throw 'Inventory duplicate count overflow was not refused before write.' }
$badConflict = Join-Path $output 'conflict.esp'
& $dotnet $cli npc patch --edition fallout4 --input-plugin $fo4 --output $badConflict --form-id 0x800 --add-inventory "$fo4Name|0x801=1" --remove-inventory "$fo4Name|0x801" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $badConflict)) { throw 'Conflicting inventory operations were not refused before write.' }
Write-Output "P03 INVENTORY FIXTURES PASS $output"
