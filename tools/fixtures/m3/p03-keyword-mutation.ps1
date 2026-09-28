param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generatorProject = Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj'
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_keyword_mutation.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-keyword-mutation-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Keyword evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build $generatorProject --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
$fo4 = Join-Path $output 'M3KeywordsFO4.esp'; $sse = Join-Path $output 'M3KeywordsSSE.esp'
& $dotnet $generator '--keywords' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Keyword fixture generation failed.' }

function Invoke-Allowed([string]$edition, [string]$source, [string]$keywords, [string]$appr) {
    $plugin = Split-Path $source -Leaf; $name = [IO.Path]::GetFileNameWithoutExtension($plugin); $outputPlugin = Join-Path $output "$name-mutated.esp"; $evidence = Join-Path $output "$edition-allowed.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $args = @($cli,'npc','patch','--edition',$edition,'--input-plugin',$source,'--output',$outputPlugin,'--form-id','0x00000800','--keywords',"$plugin|0x00000802",'--expected-sha256',$hash,'--apply','--json')
    if ($edition -eq 'fallout4') { $args += @('--appr',"$plugin|0x00000802") }
    & $dotnet @args | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Keyword mutation failed for $edition." }
    $verify = @($verifier,'--source',$source,'--output',$outputPlugin,'--json',$evidence,'--form-id','0x00000800','--plugin',$plugin,'--keywords',"$plugin|0x00000802")
    if (-not [string]::IsNullOrEmpty($appr)) { $verify += @('--appr', $appr) }
    & $python @verify
    if ($LASTEXITCODE -ne 0) { throw "Independent keyword verifier failed for $edition." }
}
$fo4Name = Split-Path -Leaf $fo4; $sseName = Split-Path -Leaf $sse
Invoke-Allowed 'fallout4' $fo4 "$fo4Name|0x00000802" "$fo4Name|0x00000802"
Invoke-Allowed 'skyrimse' $sse "$sseName|0x00000802" ''

$opsOutput = Join-Path $output 'M3KeywordsFO4-ops.esp'; $opsEvidence = Join-Path $output 'fallout4-ops.json'; $hash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
$opsArgs = @($cli,'npc','patch','--edition','fallout4','--input-plugin',$fo4,'--output',$opsOutput,'--form-id','0x00000800','--add-keyword',"$fo4Name|0x00000802",'--remove-appr',"$fo4Name|0x00000803",'--expected-sha256',$hash,'--apply','--json')
& $dotnet @opsArgs | Out-File -LiteralPath $opsEvidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Keyword add/remove mutation failed.' }
$opsVerify = @($verifier,'--source',$fo4,'--output',$opsOutput,'--json',$opsEvidence,'--form-id','0x00000800','--plugin',$fo4Name,'--keywords',"$fo4Name|0x00000801,$fo4Name|0x00000802")
& $python @opsVerify
if ($LASTEXITCODE -ne 0) { throw 'Independent keyword add/remove verification failed.' }

$refused = Join-Path $output 'sse-appr-refused.esp'; $hash = (Get-FileHash -LiteralPath $sse -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc patch --edition skyrimse --input-plugin $sse --output $refused --form-id 0x00000800 --appr 'M3KeywordsSSE.esp|0x00000802' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $refused)) { throw 'Skyrim APPR was not refused.' }
Write-Output "P03 KEYWORD FIXTURES PASS $output"
