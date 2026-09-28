$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_face_pose.py')).Path
$root = Join-Path $project '03-builds\work\m5-face-pose-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$fo4 = Join-Path $root 'fo4-pose.json'
$sse = Join-Path $root 'sse-pose.json'
$source = @'
{"version":1,"game":"fallout4","npc":"0x00000800","facialMorphIntensity":2,"regions":[{"id":4,"name":"Brow","default":{"position":[0,0,0],"rotation":[0,0,0],"scale":[0,0,0]},"bones":[{"bone":"Brow","min":{"position":[-1,-2,-3],"rotation":[0,0,0],"scale":[-0.1,-0.2,-0.3]},"max":{"position":[1,2,3],"rotation":[0,0,0],"scale":[0.1,0.2,0.3]}}]}],"faceMorphs":[{"regionId":4,"position":[0.5,0,0],"rotation":[0,0,0],"scale":-0.5}],"vertexMorphs":[{"resolver":"face","name":"A","weight":0.5,"vertices":[{"index":1,"delta":[2,0,0]}]},{"resolver":"body","name":"B","weight":1,"vertices":[{"index":1,"delta":[0,3,0]},{"index":2,"delta":[1,1,1]}]}]}
'@
$source | Out-File -LiteralPath $fo4 -Encoding utf8
$source.Replace('fallout4','skyrimse').Replace('0x00000800','0x00000801') | Out-File -LiteralPath $sse -Encoding utf8
foreach ($case in @(@{ Name='fo4'; Game='fallout4'; Npc='0x800'; Input=$fo4 }, @{ Name='sse'; Game='skyrimse'; Npc='0x801'; Input=$sse })) {
    $evidence = Join-Path $root "$($case.Name)-evidence.json"
    $hash = (Get-FileHash $case.Input -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli face pose resolve --game $case.Game --npc $case.Npc --preset $case.Input --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "face pose resolve failed for $($case.Name)" }
    python $verifier --input $case.Input --evidence $evidence
    if ($LASTEXITCODE -ne 0) { throw "face pose independent verification failed for $($case.Name)" }
}

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$badUnknown = Join-Path $negative 'unknown.json'; $source.Replace('"version":1,', '"unexpected":1,"version":1,') | Out-File -LiteralPath $badUnknown -Encoding utf8
$badDuplicate = Join-Path $negative 'duplicate.json'; $source.Replace('{"index":2,"delta":[1,1,1]}', '{"index":1,"delta":[1,1,1]}') | Out-File -LiteralPath $badDuplicate -Encoding utf8
$badNonFinite = Join-Path $negative 'nonfinite.json'; $source.Replace('"facialMorphIntensity":2', '"facialMorphIntensity":NaN') | Out-File -LiteralPath $badNonFinite -Encoding utf8
$cases = @(
    @{ Name='unknown'; Input=$badUnknown; Game='fallout4'; Npc='0x800' },
    @{ Name='duplicate'; Input=$badDuplicate; Game='fallout4'; Npc='0x800' },
    @{ Name='nonfinite'; Input=$badNonFinite; Game='fallout4'; Npc='0x800' },
    @{ Name='game-mismatch'; Input=$fo4; Game='skyrimse'; Npc='0x800' },
    @{ Name='npc-mismatch'; Input=$fo4; Game='fallout4'; Npc='0x801' }
)
foreach ($case in $cases) {
    $out = Join-Path $negative "outputs\$($case.Name).json"
    & $dotnet $cli face pose resolve --game $case.Game --npc $case.Npc --preset $case.Input --json | Out-Null
    if ($LASTEXITCODE -eq 0) { throw "negative face-pose case unexpectedly succeeded: $($case.Name)" }
    if (Test-Path $out) { throw "negative face-pose case wrote output: $($case.Name)" }
}
python -m py_compile $verifier
Write-Output "P04 FACE POSE FIXTURES PASS $root"
