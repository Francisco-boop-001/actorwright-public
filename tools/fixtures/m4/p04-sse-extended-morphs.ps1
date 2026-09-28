$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_skyrim_extended_morphs.py')).Path
$root = Join-Path $project '03-builds\work\p04-sse-extended-morphs-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null
$source = Join-Path $root 'source\P04ExtendedMorph.jslot'; New-Item -ItemType Directory -Force (Split-Path $source) | Out-Null
$sourceDocument = [ordered]@{
    version = 1
    unknown = [ordered]@{ keep = @(1, 2) }
    customMorphs = @(
        [ordered]@{ name = 'Known'; value = [single]0.1 }
        [ordered]@{ name = 'Remove'; value = [single]0.25 }
    )
}
$sourceDocument | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $source -Encoding utf8
$expected = [ordered]@{
    morphs = @(
        [ordered]@{ name = 'Known'; value = [single]0.5 }
        [ordered]@{ name = 'Remove'; value = [single]0 }
        [ordered]@{ name = 'Uncatalogued'; value = [single]-0.4 }
    )
}
$expectedPath = Join-Path $root 'expected.json'; $expected | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $expectedPath -Encoding utf8
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$outputDir = Join-Path $root 'output'; New-Item -ItemType Directory -Force $outputDir | Out-Null
$output = Join-Path $outputDir 'P04ExtendedMorph.jslot'; $evidence = Join-Path $root 'apply.json'
& $dotnet $cli face morph extended --game skyrimse --input $source --output $output --extended "@$expectedPath" --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE extended morph patch did not apply' }
python $verifier --before $source --after $output --json $evidence --expected $expectedPath
if ($LASTEXITCODE -ne 0) { throw 'SSE extended morph verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
$badRange = [ordered]@{ morphs = @([ordered]@{ name = 'Known'; value = [single]2 }) }
$badDuplicate = [ordered]@{ morphs = @([ordered]@{ name = 'Known'; value = [single]0 }, [ordered]@{ name = 'known'; value = [single]1 }) }
$cases = @(
    @{ Name = 'stale-hash'; Game = 'skyrimse'; Object = $expected; Hash = ('0' * 64) },
    @{ Name = 'range'; Game = 'skyrimse'; Object = $badRange; Hash = $hash },
    @{ Name = 'duplicate'; Game = 'skyrimse'; Object = $badDuplicate; Hash = $hash },
    @{ Name = 'wrong-game'; Game = 'fallout4'; Object = $expected; Hash = $hash }
)
foreach ($case in $cases) {
    $dir = Join-Path $negative $case.Name; New-Item -ItemType Directory -Force $dir | Out-Null
    $path = Join-Path $dir 'morphs.json'; $case.Object | ConvertTo-Json -Depth 8 | Out-File -LiteralPath $path -Encoding utf8
    $out = Join-Path $dir 'P04ExtendedMorph.jslot'
    & $dotnet $cli face morph extended --game $case.Game --input $source --output $out --extended "@$path" --expected-sha256 $case.Hash --apply --json | Out-Null
    if ($LASTEXITCODE -eq 0 -or (Test-Path $out)) { throw "negative case unexpectedly applied: $($case.Name)" }
}
$existing = Join-Path $negative 'existing\P04ExtendedMorph.jslot'; New-Item -ItemType Directory -Force (Split-Path $existing) | Out-Null; Copy-Item $output $existing
& $dotnet $cli face morph extended --game skyrimse --input $source --output $existing --extended "@$expectedPath" --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing-output negative case unexpectedly applied' }

python -m py_compile $verifier
Write-Output "P04 SSE EXTENDED MORPH FIXTURES PASS $root"
