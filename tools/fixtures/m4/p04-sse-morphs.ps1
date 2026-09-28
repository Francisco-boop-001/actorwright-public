$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generator = (Resolve-Path (Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_skyrim_face_morphs.py')).Path
$root = Join-Path $project '03-builds\work\p04-sse-morphs-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$source = Join-Path $root 'source\P04SseMorph.esp'; & $dotnet $generator --sse-morphs $source
if ($LASTEXITCODE -ne 0) { throw 'SSE morph fixture generation failed' }
$expected = [ordered]@{
    nam9 = @([single]0.25, [single]-0.25, [single]0, [single]0.75, [single]-0.5, [single]0.1, [single]-0.1, [single]0.2, [single]-0.2, [single]0.3, [single]-0.3, [single]0.4, [single]-0.4, [single]0.5, [single]-0.5, [single]0.6, [single]-0.6, [single]0.7)
    nam9Trailing = [single]::MaxValue
    nama = @([uint32]::MaxValue, [uint32]3, [uint32]::MaxValue, [uint32]15)
}
$expectedPath = Join-Path $root 'expected.json'; $expected | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $expectedPath -Encoding utf8
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$outputDir = Join-Path $root 'output'; New-Item -ItemType Directory -Force $outputDir | Out-Null
$output = Join-Path $outputDir 'P04SseMorph.esp'; $evidence = Join-Path $root 'apply.json'
& $dotnet $cli face morph patch --game skyrimse --plugin $source --output $output --npc 0x800 --vanilla "@$expectedPath" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE face morph patch did not apply' }
python $verifier --before $source --after $output --json $evidence --expected $expectedPath --form-id 0x800
if ($LASTEXITCODE -ne 0) { throw 'SSE raw face morph verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$badRange = [ordered]@{ nam9 = @([single]2) * 18; nam9Trailing = [single]::MaxValue; nama = @([uint32]::MaxValue, 0, 0, 0) }
$badNama = [ordered]@{ nam9 = $expected.nam9; nam9Trailing = [single]::MaxValue; nama = @([uint32]16, 0, 0, 0) }
$badShape = [ordered]@{ nam9 = @([single]0) * 17; nam9Trailing = [single]::MaxValue; nama = $expected.nama }
$cases = @(
    @{ Name = 'stale-hash'; Game = 'skyrimse'; Object = $expected; Hash = ('0' * 64) },
    @{ Name = 'range'; Game = 'skyrimse'; Object = $badRange; Hash = $hash },
    @{ Name = 'nama-range'; Game = 'skyrimse'; Object = $badNama; Hash = $hash },
    @{ Name = 'shape'; Game = 'skyrimse'; Object = $badShape; Hash = $hash },
    @{ Name = 'wrong-game'; Game = 'fallout4'; Object = $expected; Hash = $hash }
)
foreach ($case in $cases) {
    $dir = Join-Path $negative $case.Name; New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'morphs.json'; $case.Object | ConvertTo-Json -Depth 4 | Out-File -LiteralPath $path -Encoding utf8
    $out = Join-Path $dir 'P04SseMorph.esp'
    & $dotnet $cli face morph patch --game $case.Game --plugin $source --output $out --npc 0x800 --vanilla "@$path" --expected-sha256 $case.Hash --apply --json | Out-Null
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($case.Name)" }
}
$existing = Join-Path $negative 'existing\P04SseMorph.esp'; New-Item -ItemType Directory -Force (Split-Path $existing) | Out-Null; Copy-Item $output $existing
& $dotnet $cli face morph patch --game skyrimse --plugin $source --output $existing --npc 0x800 --vanilla "@$expectedPath" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }

python -m py_compile $verifier
Write-Output "P04 SSE FACE MORPH FIXTURES PASS $root"
