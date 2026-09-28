param([string]$OutputRoot = "")
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_template_materialization.py')).Path
$python = (Get-Command python -ErrorAction Stop).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p03-template-materialize-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Template evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
& $dotnet build (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
& $dotnet build (Join-Path $projectRoot 'src\NpcManager.Cli\NpcManager.Cli.csproj') --configuration Release --no-restore --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed.' }
$fo4 = Join-Path $output 'M3TemplateFO4.esp'; $sse = Join-Path $output 'M3TemplateSSE.esp'
& $dotnet $generator '--templates' $fo4 $sse
if ($LASTEXITCODE -ne 0) { throw 'Template fixture generation failed.' }

function Invoke-Case([string]$source, [string]$game, [string]$name) {
    $outputPlugin = Join-Path $output "$name.esp"; $evidence = Join-Path $output "$name.json"
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli npc materialize-template --game $game --plugin $source --output $outputPlugin --form-id 0x800 --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Template materialization failed for $name." }
    & $python $verifier --source $source --output $outputPlugin --json $evidence --form-id 0x800 --game $game
    if ($LASTEXITCODE -ne 0) { throw "Independent template verifier failed for $name." }
}

Invoke-Case $fo4 'fallout4' 'fallout4-apply'
Invoke-Case $sse 'skyrimse' 'skyrimse-apply'

function Refused([string]$name, [string]$game, [string]$source, [string]$categories = 'traits') {
    $destination = Join-Path $output "$name.esp"; $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli npc materialize-template --game $game --plugin $source --output $destination --form-id 0x800 --categories $categories --expected-sha256 $hash --apply --json | Out-Null
    if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $destination)) { throw "Template refusal failed for $name." }
}

& $dotnet $generator '--templates-unsupported' (Join-Path $output 'M3TemplateUnsupportedFO4.esp') (Join-Path $output 'M3TemplateUnsupportedSSE.esp')
if ($LASTEXITCODE -ne 0) { throw 'Unsupported template fixture generation failed.' }
Refused 'fallout4-unsupported-traits' 'fallout4' (Join-Path $output 'M3TemplateUnsupportedFO4.esp')
Refused 'skyrim-unsupported-traits' 'skyrimse' (Join-Path $output 'M3TemplateUnsupportedSSE.esp')

& $dotnet $generator '--templates-cycle' (Join-Path $output 'M3TemplateCycleFO4.esp') (Join-Path $output 'M3TemplateCycleSSE.esp')
if ($LASTEXITCODE -ne 0) { throw 'Cycle template fixture generation failed.' }
Refused 'fallout4-cycle' 'fallout4' (Join-Path $output 'M3TemplateCycleFO4.esp') 'stats'
Refused 'skyrim-cycle' 'skyrimse' (Join-Path $output 'M3TemplateCycleSSE.esp') 'stats'

& $dotnet $generator '--templates-missing' (Join-Path $output 'M3TemplateMissingFO4.esp') (Join-Path $output 'M3TemplateMissingSSE.esp')
if ($LASTEXITCODE -ne 0) { throw 'Missing-source template fixture generation failed.' }
Refused 'fallout4-missing-source' 'fallout4' (Join-Path $output 'M3TemplateMissingFO4.esp') 'stats'
Refused 'skyrim-missing-source' 'skyrimse' (Join-Path $output 'M3TemplateMissingSSE.esp') 'stats'

$staleDestination = Join-Path $output 'stale-hash.esp'; $staleHash = ('0' * 64)
& $dotnet $cli npc materialize-template --game fallout4 --plugin $fo4 --output $staleDestination --form-id 0x800 --expected-sha256 $staleHash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $staleDestination)) { throw 'Stale-hash refusal failed.' }

$existingDestination = Join-Path $output 'existing-output.esp'; Copy-Item -LiteralPath $fo4 -Destination $existingDestination
$existingHash = (Get-FileHash -LiteralPath $fo4 -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc materialize-template --game fallout4 --plugin $fo4 --output $existingDestination --form-id 0x800 --expected-sha256 $existingHash --apply --json | Out-Null
if ($LASTEXITCODE -ne 4 -or -not (Test-Path -LiteralPath $existingDestination)) { throw 'Existing-output refusal failed.' }

Write-Output "P03 TEMPLATE FIXTURES PASS $output"
