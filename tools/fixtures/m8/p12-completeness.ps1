$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$dotnet = Join-Path $projectRoot '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$work = Join-Path $projectRoot '03-builds\work\m8-p12-completeness'
$batch = Join-Path $work 'facegen-batch.json'
$manifest = Join-Path $projectRoot '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$sourceRoot = Join-Path $projectRoot '02-normalized-resources\upstream-papyrus'
$faceTintManifest = Join-Path $work 'face-tint-manifest.json'
$preset = Join-Path $projectRoot '01-source-copies\m4-fixtures\fo4-looksmenu.json'
$sourcePlugin = Join-Path $projectRoot '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp'
$guiXaml = Join-Path $projectRoot 'src\NpcManager.Desktop\MainWindow.xaml'
$guiProject = Join-Path $projectRoot 'src\NpcManager.Desktop\NpcManager.Desktop.csproj'
$outputRoot = Join-Path $work 'pipeline'

if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
New-Item -ItemType Directory -Path $work, $outputRoot | Out-Null
if (-not (Test-Path -LiteralPath $cli)) { throw "CLI build was not found: $cli" }
Set-Content -LiteralPath $batch -Value (@{
    schemaVersion = 1
    edition = 'fallout4'
    manifests = @($manifest, (Join-Path $projectRoot '01-source-copies\m5-fixtures\zero-shapes.json'))
} | ConvertTo-Json -Depth 4)
Set-Content -LiteralPath $faceTintManifest -Value @'
{
  "schemaVersion": "1",
  "npcFormId": "0x00000800",
  "width": 512,
  "height": 512,
  "format": "bgra8",
  "mipCount": 1,
  "alphaMode": "preserve",
  "baseColor": [0.2, 0.4, 0.6, 0.5],
  "layers": [{"name":"complexion","source":"textures/complexion.dds","provider":"fixture","blend":"over","opacity":0.5,"color":[1,0,0,1]}],
  "probes": [{"x":0,"y":0},{"x":511,"y":511}]
}
'@

function Invoke-Npcm([string]$name, [string[]]$arguments, [int]$expectedExit = 0) {
    $captured = @(& $dotnet $cli @arguments 2>&1)
    $exit = $LASTEXITCODE
    $text = $captured -join [Environment]::NewLine
    $path = Join-Path $work "$name.json"
    Set-Content -LiteralPath $path -Value $text -Encoding UTF8
    if ($exit -ne $expectedExit) { throw "$name returned $exit; expected $expectedExit`n$text" }
    return $path
}

$capabilities = Invoke-Npcm 'capabilities' @('capabilities', '--json')
$schema = Invoke-Npcm 'schema-export' @('schema', 'export', '--output', (Join-Path $work 'all.schema.json'), '--json')
$gui = Invoke-Npcm 'gui' @('gui', '--json')
$diagnose = Invoke-Npcm 'facegen-diagnose' @('facegen', 'diagnose', '--edition', 'fallout4', '--manifest', $manifest, '--npc', '0x00000800', '--json')
$geom = Invoke-Npcm 'facegen-build-geom' @('facegen', 'build-geom', '--edition', 'fallout4', '--manifest', $manifest, '--output', (Join-Path $work 'facegeom.json'), '--json')
$batchResult = Invoke-Npcm 'facegen-bake-all' @('facegen', 'bake-all', '--edition', 'fallout4', '--batch', $batch, '--output', (Join-Path $work 'batch.json'), '--json')
$runtimeScript = Invoke-Npcm 'runtime-script-build' @('runtime-script', 'build', '--edition', 'fallout4', '--source-root', $sourceRoot, '--output', (Join-Path $work 'script-build.runtime-script-build.json'), '--json')
$runtimePackage = Invoke-Npcm 'runtime-script-package' @('runtime-script', 'package', '--edition', 'fallout4', '--source-root', $sourceRoot, '--output-root', (Join-Path $work 'runtime-script-package'), '--json')
$pipeline = Invoke-Npcm 'pipeline' @('pipeline', 'preset-to-npc', '--format', 'looksmenu', '--edition', 'fallout4', '--preset', $preset,
    '--source-plugin', $sourcePlugin, '--plugin', 'P12Pipeline.esp', '--npc', '0x00000800', '--mod-name', 'P12Pipeline.esp',
    '--output-root', $outputRoot, '--editor-id', 'P12PipelineNpc', '--facegeom-manifest', $manifest,
    '--facetint-manifest', $faceTintManifest, '--runtime-script-build', (Join-Path $work 'script-build.runtime-script-build.json'),
    '--runtime-script-package', (Join-Path $work 'runtime-script-package\runtime-script-package.json'), '--json')

python (Join-Path $projectRoot 'tools\verification\verify_p12.py') --project-root $projectRoot --work-root $work --gui-xaml $guiXaml --gui-project $guiProject
if ($LASTEXITCODE -ne 0) { throw 'P12 independent verifier failed.' }
Write-Output "P12 COMPLETENESS PASS $work"
