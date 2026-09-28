$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_skyrim_sculpt.py')).Path
$root = Join-Path $project '03-builds\work\m5-sse-sculpt-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$source = Join-Path $root 'source\P04SculptTest.jslot'; New-Item -ItemType Directory -Force (Split-Path $source) | Out-Null
@'
{"version":4,"unknown":{"keep":[1,2]},"morphs":{"default":{"morphs":[0.1]},"custom":[{"name":"Keep","value":0.2}],"sculptDivisor":10000,"sculpt":[{"host":"FemaleHeadCharGen.tri","vertices":120,"data":[[3,100,-200,300]]}]}}
'@ | Out-File -LiteralPath $source -Encoding utf8
$expected = Join-Path $root 'expected.json'
@'
{"divisor":20000,"parts":[{"host":"FemaleHeadCharGen.tri","vertices":120,"verts":[{"index":3,"dx":0.01,"dy":-0.02,"dz":0.03}]},{"host":"FemaleHeadBrowsCharGen.tri","vertices":80,"verts":[{"index":2,"dx":-0.005,"dy":0.004,"dz":0}]}]}
'@ | Out-File -LiteralPath $expected -Encoding utf8
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$outputDir = Join-Path $root 'output'; New-Item -ItemType Directory -Force $outputDir | Out-Null
$output = Join-Path $outputDir 'P04SculptTest.jslot'; $evidence = Join-Path $root 'apply.json'
& $dotnet $cli face sculpt patch --game skyrimse --input $source --output $output --sculpt "@$expected" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE sculpt patch did not apply' }
python $verifier --before $source --after $output --json $evidence --expected $expected
if ($LASTEXITCODE -ne 0) { throw 'SSE sculpt verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$badDuplicate = '{"divisor":10000,"parts":[{"host":"Head.tri","vertices":10,"verts":[{"index":1,"dx":0,"dy":0,"dz":0},{"index":1,"dx":0,"dy":0,"dz":0}]}]}'
$badRange = '{"divisor":10000,"parts":[{"host":"Head.tri","vertices":10,"verts":[{"index":10,"dx":0,"dy":0,"dz":0}]}]}'
$empty = '{"divisor":10000,"parts":[]}'
$cases = @(
    @{ Name = 'stale-hash'; Game = 'skyrimse'; Json = (Get-Content -Raw $expected); Hash = ('0' * 64) },
    @{ Name = 'duplicate'; Game = 'skyrimse'; Json = $badDuplicate; Hash = $hash },
    @{ Name = 'range'; Game = 'skyrimse'; Json = $badRange; Hash = $hash },
    @{ Name = 'empty'; Game = 'skyrimse'; Json = $empty; Hash = $hash },
    @{ Name = 'wrong-game'; Game = 'fallout4'; Json = (Get-Content -Raw $expected); Hash = $hash }
)
foreach ($case in $cases) {
    $dir = Join-Path $negative $case.Name; New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'sculpt.json'; $case.Json | Out-File -LiteralPath $path -Encoding utf8
    $out = Join-Path $dir 'P04SculptTest.jslot'
    & $dotnet $cli face sculpt patch --game $case.Game --input $source --output $out --sculpt "@$path" --expected-sha256 $case.Hash --apply --json | Out-Null
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($case.Name)" }
}
$existing = Join-Path $negative 'existing\P04SculptTest.jslot'; New-Item -ItemType Directory -Force (Split-Path $existing) | Out-Null; Copy-Item $output $existing
& $dotnet $cli face sculpt patch --game skyrimse --input $source --output $existing --sculpt "@$expected" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }

python -m py_compile $verifier
Write-Output "P04 SSE SCULPT FIXTURES PASS $root"
