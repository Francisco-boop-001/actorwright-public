param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_sse_overlay_fold.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\m5-sse-layer-fold-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'SSE layer-fold evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$manifest = Join-Path $output 'fold.json'
$json = @'
{"base":{"width":1,"height":1,"pixels":[0.2,0.4,0.6,0.5]},"facetint":{"width":1,"height":1,"pixels":[0.2470588235294118,0.2509803921568627,0.2470588235294118,1]},"layers":[{"source":"skeeMask","layerType":1,"blend":"normal","color":[0,1,0,0.5],"opacity":0.5,"texture":{"width":1,"height":1,"pixels":[0.5,0,0,1]}},{"source":"faceOverlay","node":"Face [Ovl1]","color":[1,1,1,1],"opacity":0.5,"texture":{"width":1,"height":1,"pixels":[1,0,0,0.5]}},{"source":"faceOverlay","node":"Face [Ovl0]","color":[1,1,1,1],"opacity":1,"texture":{"width":1,"height":1,"pixels":[0,0,1,1]}}]}
'@
[IO.File]::WriteAllText($manifest, $json, (New-Object Text.UTF8Encoding($false)))
$first = Join-Path $output 'fold-1.dds'
$firstResponse = Join-Path $output 'fold-1-response.json'
& $dotnet $cli body overlay bake --game skyrimse --layers "@$manifest" --output $first --json |
    Out-File -LiteralPath $firstResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE layer-fold bake failed.' }
python $verifier --input $manifest --output $first --response $firstResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE layer-fold verification failed.' }

$second = Join-Path $output 'fold-2.dds'
$secondResponse = Join-Path $output 'fold-2-response.json'
& $dotnet $cli body overlay bake --game skyrimse --layers "@$manifest" --output $second --json |
    Out-File -LiteralPath $secondResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Second SSE layer-fold bake failed.' }
if ((Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash) {
    throw 'SSE layer-fold output hash was not deterministic.'
}
python -m py_compile $verifier
Write-Output "SSE LAYER FOLD FIXTURE PASS $output"
