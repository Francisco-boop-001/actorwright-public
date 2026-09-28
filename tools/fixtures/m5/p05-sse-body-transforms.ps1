param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_sse_body_transforms.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\m5-sse-body-transforms-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'SSE body-transform evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Json([string]$path, [string]$json) { [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false))) }

$input = Join-Path $output 'source.jslot'
Write-Json $input '{"marker":{"keep":true},"transforms":[{"firstPerson":false,"node":"NPC Spine [Spn0]","keys":[{"name":"RSMTransform","values":[{"key":30,"type":4,"index":2,"data":1.02},{"key":31,"type":4,"index":0,"data":0.1},{"key":31,"type":4,"index":1,"data":0.2},{"key":31,"type":4,"index":2,"data":0.3},{"key":32,"type":4,"index":0,"data":1},{"key":32,"type":4,"index":1,"data":0},{"key":32,"type":4,"index":2,"data":0},{"key":32,"type":4,"index":3,"data":0},{"key":32,"type":4,"index":4,"data":1},{"key":32,"type":4,"index":5,"data":0},{"key":32,"type":4,"index":6,"data":0},{"key":32,"type":4,"index":7,"data":0},{"key":32,"type":4,"index":8,"data":1},{"key":33,"type":3,"index":3,"data":0}]}]}],"skinOverrides":[{"firstPerson":false,"slotMask":32,"values":[{"key":9,"type":2,"index":0,"data":"textures\\actors\\character\\body.dds"},{"key":9,"type":2,"index":2,"data":"textures\\actors\\character\\body_detail.dds"},{"key":7,"type":3,"index":-1,"data":-65536},{"key":8,"type":4,"index":-1,"data":0.75}]}]}'
$transforms = Join-Path $output 'transforms.json'
Write-Json $transforms '[{"node":"NPC Spine [Spn0]","scale":1.1,"scaleMode":1,"position":[0,0.2,0],"rotation":[1,0,0,0,1,0,0,0,1]}]'
$skins = Join-Path $output 'skins.json'
Write-Json $skins '[{"slotMask":32,"textures":{"0":"textures\\actors\\character\\body_new.dds","2":"textures\\actors\\character\\detail_new.dds"},"tint":[1,0.5,0.25,0.8],"alpha":0.6}]'
$applied = Join-Path $output 'applied.jslot'
$response = Join-Path $output 'response.json'
& $dotnet $cli body transforms apply --game skyrimse --npc 0x123 --preset $input --transforms "@$transforms" --skin-overrides "@$skins" --output $applied --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE body-transform apply failed.' }
python $verifier --input $input --output $applied --response $response --transforms $transforms --skins $skins
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE body-transform verification failed.' }

$repeat = Join-Path $output 'applied-repeat.jslot'
$repeatResponse = Join-Path $output 'repeat-response.json'
& $dotnet $cli body transforms apply --game skyrimse --npc 0x123 --preset $input --transforms "@$transforms" --skin-overrides "@$skins" --output $repeat --json | Out-File -LiteralPath $repeatResponse -Encoding utf8
if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $applied -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $repeat -Algorithm SHA256).Hash) { throw 'SSE body-transform output was not deterministic.' }

$invalidInput = Join-Path $output 'unsupported.jslot'
(Get-Content -LiteralPath $input -Raw).Replace('"key":30', '"key":40') | Set-Content -LiteralPath $invalidInput -Encoding utf8
$invalidOutput = Join-Path $output 'invalid.jslot'
$invalidResponse = Join-Path $output 'invalid-response.json'
& $dotnet $cli body transforms apply --game skyrimse --npc 0x123 --preset $invalidInput --output $invalidOutput --json | Out-File -LiteralPath $invalidResponse -Encoding utf8
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $invalidOutput)) { throw 'Unsupported transform key was not refused.' }

python -m py_compile $verifier
Write-Output "SSE BODY TRANSFORMS FIXTURES PASS $output"
