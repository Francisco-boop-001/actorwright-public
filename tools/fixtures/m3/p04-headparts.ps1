$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = (Resolve-Path (Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_npc_face_patch.py')).Path
$root = Join-Path $project '03-builds\work\p04-headparts-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$sse = Join-Path $root 'source\P04SSE.esp'; $fo4 = Join-Path $root 'source\P04FO4.esp'
& $dotnet $generator --headparts $fo4 $sse
$partsSse = 'face=P04SSE.esp|0x811,eyes=P04SSE.esp|0x812,hair=P04SSE.esp|0x813,facial-hair=P04SSE.esp|0x814,scar=P04SSE.esp|0x815,eyebrows=P04SSE.esp|0x816,meatcaps=P04SSE.esp|0x817,teeth=P04SSE.esp|0x818,head-rear=P04SSE.esp|0x819'
$partsFo4 = $partsSse.Replace('P04SSE', 'P04FO4')
$expectedIds = '0x811,0x812,0x813,0x814,0x815,0x816,0x817,0x818,0x819'
foreach ($case in @(
    @{ Game = 'skyrimse'; Source = $sse; Parts = $partsSse; Name = 'SSE' },
    @{ Game = 'fallout4'; Source = $fo4; Parts = $partsFo4; Name = 'FO4' })) {
    $data = Join-Path (Split-Path $case.Source) 'data'
    $outDir = Join-Path $root ("output\" + $case.Name); New-Item -ItemType Directory -Force $outDir | Out-Null
    $out = Join-Path $outDir (Split-Path $case.Source -Leaf)
    $evidence = Join-Path $root ("" + $case.Name + '-apply.json')
    $hash = (Get-FileHash $case.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli npc face-patch --game $case.Game --plugin $case.Source --output $out --data-root $data --npc 0x800 --headparts $case.Parts --hair-color none --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "$($case.Name) face patch did not apply" }
    python $verifier --before $case.Source --after $out --json $evidence --form-id 0x800 --headparts $expectedIds --hair-color none
    if ($LASTEXITCODE -ne 0) { throw "$($case.Name) raw PNAM/HCLF verification failed" }
}

$noopDir = Join-Path $root 'negative\no-op'; New-Item -ItemType Directory -Force $noopDir | Out-Null
$noopSource = Join-Path $root 'output\SSE\P04SSE.esp'; $noopOutput = Join-Path $noopDir 'P04SSE.esp'
$noopHash = (Get-FileHash $noopSource -Algorithm SHA256).Hash.ToLowerInvariant()
& $dotnet $cli npc face-patch --game skyrimse --plugin $noopSource --output $noopOutput --data-root (Join-Path (Split-Path $sse) 'data') --npc 0x800 --headparts $partsSse --hair-color none --expected-sha256 $noopHash --apply --json | Out-Null
if ($LASTEXITCODE -ne 0 -or (Test-Path $noopOutput)) { throw 'no-op face patch unexpectedly wrote an output' }

# Rational refusal cases: duplicate identity, missing required type, stale hash, existing output,
# missing provider file, and a cross-plugin headpart reference must all fail without a write.
$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$source = $sse; $data = Join-Path (Split-Path $source) 'data'; $badData = Join-Path $negative 'data'; Copy-Item $data $badData -Recurse
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$valid = $partsSse
$tests = @(
    @{ Name = 'duplicate'; Parts = $valid.Replace('head-rear=P04SSE.esp|0x819', 'head-rear=P04SSE.esp|0x811'); Data = $data; Hash = $hash },
    @{ Name = 'missing-type'; Parts = $valid.Replace(',head-rear=P04SSE.esp|0x819', ''); Data = $data; Hash = $hash },
    @{ Name = 'cross-plugin'; Parts = $valid.Replace('face=P04SSE.esp|0x811', 'face=Other.esp|0x811'); Data = $data; Hash = $hash },
    @{ Name = 'missing-provider'; Parts = $valid; Data = $badData; Hash = $hash },
    @{ Name = 'stale-hash'; Parts = $valid; Data = $data; Hash = ('0' * 64) })
foreach ($test in $tests) {
    if ($test.Name -eq 'missing-provider') { Remove-Item (Join-Path $badData 'meshes\p04\1.nif') -Force }
    $caseDir = Join-Path $negative $test.Name; New-Item -ItemType Directory -Force $caseDir | Out-Null
    $out = Join-Path $caseDir 'P04SSE.esp'; $evidence = Join-Path $caseDir 'result.json'
    & $dotnet $cli npc face-patch --game skyrimse --plugin $source --output $out --data-root $test.Data --npc 0x800 --headparts $test.Parts --hair-color none --expected-sha256 $test.Hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($test.Name)" }
}
$existingDir = Join-Path $negative 'existing'; New-Item -ItemType Directory -Force $existingDir | Out-Null
$existing = Join-Path $existingDir 'P04SSE.esp'; Copy-Item (Join-Path $root 'output\SSE\P04SSE.esp') $existing
& $dotnet $cli npc face-patch --game skyrimse --plugin $source --output $existing --data-root $data --npc 0x800 --headparts $valid --hair-color none --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }
python -m py_compile $verifier
Write-Output "P04 HEADPART FIXTURES PASS $root"
