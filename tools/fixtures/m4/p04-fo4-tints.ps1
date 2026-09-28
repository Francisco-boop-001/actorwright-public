$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = (Resolve-Path (Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_npc_face_tints.py')).Path
$root = Join-Path $project '03-builds\work\p04-fo4-tints-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$source = Join-Path $root 'source\P04TintFO4.esp'
& $dotnet $generator --face-tints $source
if ($LASTEXITCODE -ne 0) { throw 'Face-tint fixture generation failed' }
$expected = @(
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x33; value = 99; color = [ordered]@{ red = 1; green = 2; blue = 3 }; templateColorIndex = 4; rawTendBase64 = [Convert]::ToBase64String([byte[]](99,1,2,3,0,4,0)) },
    [ordered]@{ dataType = 'texture-set'; optionIndex = 0x31; value = 55; rawTendBase64 = [Convert]::ToBase64String([byte[]](55)) },
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x32; value = 10; color = [ordered]@{ red = 9; green = 8; blue = 7 }; rawTendBase64 = [Convert]::ToBase64String([byte[]](10,9,8,7,7)) },
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x30; value = 75; color = [ordered]@{ red = 33; green = 44; blue = 55 }; templateColorIndex = -1; rawTendBase64 = [Convert]::ToBase64String([byte[]](75,33,44,55,0,255,255)) }
)
$expectedPath = Join-Path $root 'expected.json'; $expected | ConvertTo-Json -Depth 5 | Out-File -LiteralPath $expectedPath -Encoding utf8
$layers = $expected | ConvertTo-Json -Depth 5 -Compress
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$outputDir = Join-Path $root 'output'; New-Item -ItemType Directory -Force $outputDir | Out-Null
$output = Join-Path $outputDir 'P04TintFO4.esp'; $evidence = Join-Path $root 'apply.json'
& $dotnet $cli face tint patch --game fallout4 --plugin $source --output $output --npc 0x800 --layers "@$expectedPath" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 face-tint patch did not apply' }
python $verifier --before $source --after $output --json $evidence --expected $expectedPath --form-id 0x800
if ($LASTEXITCODE -ne 0) { throw 'FO4 raw face-tint verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$original = @(
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x30; value = 60; color = [ordered]@{ red = 10; green = 20; blue = 30 }; templateColorIndex = -1; rawTendBase64 = [Convert]::ToBase64String([byte[]](60,10,20,30,0,255,255)) },
    [ordered]@{ dataType = 'texture-set'; optionIndex = 0x31; value = 40; rawTendBase64 = [Convert]::ToBase64String([byte[]](40)) },
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x32; value = 80; color = [ordered]@{ red = 80; green = 90; blue = 100 }; rawTendBase64 = [Convert]::ToBase64String([byte[]](80,80,90,100,7)) },
    [ordered]@{ dataType = 'value-color'; optionIndex = 0x33; value = 20; color = [ordered]@{ red = 200; green = 210; blue = 220 }; templateColorIndex = 2; rawTendBase64 = [Convert]::ToBase64String([byte[]](20,200,210,220,0,2,0)) }
)
$originalLayers = $original | ConvertTo-Json -Depth 5 -Compress
$cases = @(
    @{ Name = 'stale-hash'; Game = 'fallout4'; Layers = $layers; Hash = ('0' * 64) },
    @{ Name = 'duplicate'; Game = 'fallout4'; Layers = (($expected + $expected[0]) | ConvertTo-Json -Depth 5 -Compress); Hash = $hash },
    @{ Name = 'texture-raw-missing'; Game = 'fallout4'; Layers = '[{"dataType":"texture-set","optionIndex":49,"value":55}]'; Hash = $hash },
    @{ Name = 'unsupported-skyrim'; Game = 'skyrimse'; Layers = $layers; Hash = $hash }
)
foreach ($case in $cases) {
    $dir = Join-Path $negative $case.Name; New-Item -ItemType Directory -Force $dir | Out-Null
    $out = Join-Path $dir 'P04TintFO4.esp'
    $caseLayersPath = Join-Path $dir 'layers.json'; $case.Layers | Out-File -LiteralPath $caseLayersPath -Encoding utf8
    & $dotnet $cli face tint patch --game $case.Game --plugin $source --output $out --npc 0x800 --layers "@$caseLayersPath" --expected-sha256 $case.Hash --apply --json | Out-Null
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($case.Name)" }
}
$existing = Join-Path $negative 'existing\P04TintFO4.esp'; New-Item -ItemType Directory -Force (Split-Path $existing) | Out-Null; Copy-Item $output $existing
& $dotnet $cli face tint patch --game fallout4 --plugin $source --output $existing --npc 0x800 --layers "@$expectedPath" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }

# Explicit empty lists are the CLI equivalent of the upstream Remove action.
$clear = Join-Path $negative 'clear\P04TintFO4.esp'; $clearDir = Split-Path $clear; New-Item -ItemType Directory -Force $clearDir | Out-Null
& $dotnet $cli face tint patch --game fallout4 --plugin $source --output $clear --npc 0x800 --layers '[]' --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $clear)) { throw 'explicit empty face-tint list did not apply' }

python -m py_compile $verifier
Write-Output "P04 FO4 FACE TINT FIXTURES PASS $root"
