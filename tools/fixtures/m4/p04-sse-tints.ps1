$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = (Resolve-Path (Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_skyrim_face_tints.py')).Path
$root = Join-Path $project '03-builds\work\p04-sse-tints-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$source = Join-Path $root 'source\P04SseTints.esp'; & $dotnet $generator --sse-tints $source
if ($LASTEXITCODE -ne 0) { throw 'SSE tint fixture generation failed' }
$expected = @(
    [ordered]@{ index = 4; red = 12; green = 34; blue = 56; alpha = 255; coverage = 25; presetIndex = 0 },
    [ordered]@{ index = 24; red = 90; green = 80; blue = 70; alpha = 128; coverage = 75; presetIndex = -2 }
)
$expectedPath = Join-Path $root 'expected.json'; $expected | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $expectedPath -Encoding utf8
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$outputDir = Join-Path $root 'output'; New-Item -ItemType Directory -Force $outputDir | Out-Null
$output = Join-Path $outputDir 'P04SseTints.esp'; $evidence = Join-Path $root 'apply.json'
& $dotnet $cli face tint patch --game skyrimse --plugin $source --output $output --npc 0x800 --layers "@$expectedPath" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE face tint patch did not apply' }
python $verifier --before $source --after $output --json $evidence --expected $expectedPath --form-id 0x800
if ($LASTEXITCODE -ne 0) { throw 'SSE raw face tint verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$duplicate = @($expected[0], $expected[0])
$badCoverage = @([ordered]@{ index = 4; red = 1; green = 2; blue = 3; alpha = 255; coverage = 101; presetIndex = 0 })
$cases = @(
    @{ Name = 'stale-hash'; Object = $expected; Hash = ('0' * 64) },
    @{ Name = 'duplicate'; Object = $duplicate; Hash = $hash },
    @{ Name = 'coverage'; Object = $badCoverage; Hash = $hash }
)
foreach ($case in $cases) {
    $dir = Join-Path $negative $case.Name; New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'layers.json'; ConvertTo-Json -InputObject $case.Object -Depth 4 | Out-File -LiteralPath $path -Encoding utf8
    $out = Join-Path $dir 'P04SseTints.esp'
    & $dotnet $cli face tint patch --game skyrimse --plugin $source --output $out --npc 0x800 --layers "@$path" --expected-sha256 $case.Hash --apply --json | Out-Null
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($case.Name)" }
}
$existing = Join-Path $negative 'existing\P04SseTints.esp'; New-Item -ItemType Directory -Force (Split-Path $existing) | Out-Null; Copy-Item $output $existing
& $dotnet $cli face tint patch --game skyrimse --plugin $source --output $existing --npc 0x800 --layers "@$expectedPath" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }

python -m py_compile $verifier
Write-Output "P04 SSE FACE TINT FIXTURES PASS $root"
