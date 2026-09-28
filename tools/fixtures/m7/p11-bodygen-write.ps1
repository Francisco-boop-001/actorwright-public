param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_bodygen_sidecars.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p11-bodygen-write-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'BodyGen evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

$fixtures = @(
    @{ Edition = 'fallout4'; Plugin = 'P11BodyGenFO4.esp'; Mod = 'P11BodyGenFO4'; Morphs = '[{"name":"Calf","value":0.25},{"name":"Waist","value":-0.1}]' },
    @{ Edition = 'skyrimse'; Plugin = 'P11BodyGenSSE.esp'; Mod = 'P11BodyGenSSE'; Morphs = '[{"name":"Breast","value":0.4}]' }
)
foreach ($fixture in $fixtures) {
    $assignments = Join-Path $output "$($fixture.Edition)-assignments.json"
    $json = "{`"schemaVersion`":1,`"plugin`":`"$($fixture.Plugin)`",`"npc`":`"0x000800`",`"modName`":`"$($fixture.Mod)`",`"morphs`":$($fixture.Morphs)}"
    [IO.File]::WriteAllText($assignments, $json, (New-Object Text.UTF8Encoding($false)))
    $evidence = Join-Path $output "$($fixture.Edition).json"
    & $dotnet $cli bodygen write --game $fixture.Edition --assignments "@$assignments" --output $output --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "BodyGen write failed for $($fixture.Edition)." }
    & $python $verifier --root $output --edition $fixture.Edition --plugin $fixture.Plugin --form-id 0x000800 --mod-name $fixture.Mod
    if ($LASTEXITCODE -ne 0) { throw "Independent BodyGen verifier failed for $($fixture.Edition)." }
}
Write-Output "P11 BODYGEN WRITE FIXTURES PASS $output"
