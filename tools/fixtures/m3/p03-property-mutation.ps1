param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_property_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-property-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Property evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3PropertiesFO4.esp'; $sse = Join-Path $output 'M3PropertiesSSE.esp'
& $dotnet $generator '--properties' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Property fixture generation failed.' }

function Invoke-Case([string]$source, [string[]]$mutation, [string]$expected, [string]$name) {
    $plugin = Split-Path $source -Leaf; $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition','fallout4','--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800') + $mutation + @('--expected-sha256',$hash,'--apply','--json')
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Property mutation failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x00000800 --plugin $plugin --properties $expected
    if ($LASTEXITCODE -ne 0) { throw "Independent property verifier failed for $name." }
}

$fo4Name = Split-Path -Leaf $fo4
Invoke-Case $fo4 @('--properties', "$fo4Name|0x00000801=3.5,$fo4Name|0x00000803=-4.25") "$fo4Name|0x00000801=3.5,$fo4Name|0x00000803=-4.25" 'fallout4-replace'
Invoke-Case $fo4 @('--add-property', "$fo4Name|0x00000803=4.5", '--update-property', "$fo4Name|0x00000801=2.75", '--remove-property', "$fo4Name|0x00000802") "$fo4Name|0x00000801=2.75,$fo4Name|0x00000803=4.5" 'fallout4-operations'

$hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
function Refused([string]$name, [int]$code, [string[]]$mutation, [string]$edition = 'fallout4') {
    $dest = Join-Path $output "$name.esp"
    & $dotnet $cli npc patch --edition $edition --input-plugin $(if ($edition -eq 'fallout4') { $fo4 } else { $sse }) --output $dest --form-id 0x800 @mutation --expected-sha256 $hash --apply --json | Out-Null
    if ($LASTEXITCODE -ne $code -or (Test-Path -LiteralPath $dest)) { throw "Property refusal failed for $name." }
}
Refused 'duplicate' 2 @('--add-property', "$fo4Name|0x801=1,$fo4Name|0x801=2")
Refused 'external' 4 @('--add-property', 'Other.esp|0x801=1')
Refused 'conflict' 4 @('--add-property', "$fo4Name|0x801=1", '--remove-property', "$fo4Name|0x801")
Refused 'nonfinite' 4 @('--add-property', "$fo4Name|0x803=NaN")
$sseName = Split-Path -Leaf $sse
Refused 'skyrim-unsupported' 4 @('--add-property', "$sseName|0x801=1") 'skyrimse'
Write-Output "P03 PROPERTY FIXTURES PASS $output"
