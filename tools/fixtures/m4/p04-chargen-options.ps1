$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_facegen_options.py')).Path
$root = Join-Path $project '03-builds\work\p04-chargen-options-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

$generator = @'
import json
import sys

path, game = sys.argv[1], sys.argv[2]
fo4 = game == "fallout4"
bucket = {
    "workingSpace": "g22" if fo4 else "linear",
    "compositeSpace": "linear",
    "sourceSpace": "g22" if fo4 else "linear",
    "outputSpace": "g22" if fo4 else "linear",
    "maskConversion": "g22Encode" if fo4 else "raw",
    "framework": "overPrev",
    "softLight": "gimp",
    "maskChannel": "byKind" if fo4 else "r",
}
normal = {
    "workingSpace": "linear", "compositeSpace": "linear", "sourceSpace": "linear",
    "outputSpace": "linear", "maskConversion": "g22Encode" if fo4 else "raw",
    "framework": "overPrev", "softLight": "gimp", "maskChannel": "byKind" if fo4 else "r",
}
swap = {
    "workingSpace": "g22" if fo4 else "linear", "compositeSpace": "linear",
    "sourceSpace": "srgb" if fo4 else "linear", "outputSpace": "g22" if fo4 else "linear",
    "maskConversion": "g22Encode" if fo4 else "raw", "framework": "overPrev", "softLight": "gimp",
    "maskChannel": "byKind" if fo4 else "r",
}
document = {
    "schemaVersion": "1", "edition": game, "perLayerResolution": False,
    "diffuseResolution": "inherit", "normalResolution": "inherit", "specularResolution": "inherit",
    "diffuseCompression": "bc3",
    "normalCompression": "bc5" if fo4 else "uncompressed", "specularCompression": "bc5",
    "generateTga": False, "applyGhoulHeadRearFix": False,
    "applyEyebrowsFixedColor": fo4, "applyMouthVanillaFix": False,
    "bakeSseRaceMenuOverlays": not fo4,
    "convention": {
        "diffuse": bucket, "normalSpecular": normal, "swap": swap,
        "diffuseWorkingSpaceByBlend": {
            "replace": "linear", "multiply": "linear", "overlay": "linear",
            "softLight": "g22", "hardLight": "linear",
        },
        "diffuseTextureSourceSpace": "srgb" if fo4 else "linear",
        "seedDiffuseG22": fo4, "seedMode": "baseTexture" if fo4 else "constant",
        "seedConstant": [0.5, 0.5, 0.5],
    },
    "tintSort": {
        "tintRules": ([{"key": 1, "descending": True}, {"key": 2, "descending": True}] if fo4 else [{"key": 0, "descending": False}]),
        "swapRules": ([{"key": 0, "descending": False}, {"key": 1, "descending": False}] if fo4 else [{"key": 0, "descending": False}]),
        "skinTonePlacement": "positional",
    },
}
with open(path, "w", encoding="utf-8", newline="\n") as stream:
    json.dump(document, stream, indent=2)
    stream.write("\n")
'@

foreach ($game in @('fallout4', 'skyrimse')) {
    $source = Join-Path $root "$game-options.json"
    $output = Join-Path $root "$game-options-canonical.json"
    $evidence = Join-Path $root "$game-apply.json"
    $generator | python - $source $game
    $hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
    & $dotnet $cli facegen options --game $game --input $source --output $output --expected-sha256 $hash --apply --json | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "$game CharGen options did not apply" }
    python $verifier --input $source --output $output --evidence $evidence --game $game
    if ($LASTEXITCODE -ne 0) { throw "$game CharGen options independent verification failed" }
}

$source = Join-Path $root 'fallout4-options.json'
$hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
$stale = Join-Path $root 'negative-stale.json'
& $dotnet $cli facegen options --game fallout4 --input $source --output $stale --expected-sha256 ('0' * 64) --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $stale)) { throw 'stale hash case unexpectedly applied' }

$existing = Join-Path $root 'fallout4-options-canonical.json'
& $dotnet $cli facegen options --game fallout4 --input $source --output $existing --expected-sha256 $hash --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'existing output case unexpectedly applied' }

$invalid = Join-Path $root 'sse-invalid.json'
$generator | python - $invalid skyrimse
$text = [IO.File]::ReadAllText($invalid).Replace('"applyMouthVanillaFix": false', '"applyMouthVanillaFix": true')
[IO.File]::WriteAllText($invalid, $text, (New-Object Text.UTF8Encoding($false)))
$invalidOut = Join-Path $root 'sse-invalid-output.json'
& $dotnet $cli facegen options --game skyrimse --input $invalid --output $invalidOut --apply --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path $invalidOut)) { throw 'FO4-only SSE fix case unexpectedly applied' }

$duplicate = Join-Path $root 'duplicate.json'
[IO.File]::WriteAllText($duplicate, '{"schemaVersion":"1","schemaVersion":"1"}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli facegen options --game fallout4 --input $duplicate --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'duplicate JSON property case unexpectedly passed' }

$unknown = Join-Path $root 'unknown.json'
$unknownText = [IO.File]::ReadAllText($source).Replace('"schemaVersion": "1"', '"schemaVersion": "1", "unknownField": true')
[IO.File]::WriteAllText($unknown, $unknownText, (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli facegen options --game fallout4 --input $unknown --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'unknown JSON property case unexpectedly passed' }

$protected = 'F:\ExampleGame\facegen-options.json'
& $dotnet $cli facegen options --game fallout4 --input $source --output $protected --apply --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'protected output case unexpectedly passed' }

python -m py_compile $verifier
Write-Output "P04 CHARGEN OPTIONS FIXTURES PASS $root"
