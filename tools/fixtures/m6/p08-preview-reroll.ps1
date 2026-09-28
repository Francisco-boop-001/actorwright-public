$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m6-preview-reroll-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_reroll.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$hash = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'
$assets = @([ordered]@{ category = 'outfit'; path = 'meshes/outfit.nif'; provider = 'fixture'; sha256 = $hash })
$variants = @(
    [ordered]@{ id = 'a'; outfit = 'M2FixtureFO4.esp|0x801'; assets = @('meshes/outfit.nif') }
    [ordered]@{ id = 'b'; outfit = 'M2FixtureFO4.esp|0x802'; assets = @('meshes/outfit.nif') }
    [ordered]@{ id = 'c'; outfit = 'M2FixtureFO4.esp|0x803'; assets = @('meshes/outfit.nif') }
)
$manifestObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x900'; assets = $assets; variants = $variants }
$manifest = Join-Path $root 'scene.json'
[IO.File]::WriteAllText($manifest, ($manifestObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$output = Join-Path $root 'reroll-a.json'
$repeat = Join-Path $root 'reroll-b.json'
$response = Join-Path $root 'reroll-response.json'
& $dotnet $cli preview reroll --edition fallout4 --manifest $manifest --npc 0x900 --seed 42 --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Preview reroll evidence build failed' }
& $dotnet $cli preview reroll --edition fallout4 --manifest $manifest --npc 0x900 --seed 42 --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Repeated preview reroll evidence build failed' }
& python $verifier --manifest $manifest --output $output --repeat $repeat --response $response --seed 42
if ($LASTEXITCODE -ne 0) { throw 'Independent preview reroll verification failed' }

$wrongNpc = Join-Path $root 'wrong-npc.json'
& $dotnet $cli preview reroll --edition fallout4 --manifest $manifest --npc 0x901 --seed 42 --output $wrongNpc --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $wrongNpc)) { throw 'Preview reroll accepted an NPC mismatch' }

$badSeed = Join-Path $root 'bad-seed.json'
& $dotnet $cli preview reroll --edition fallout4 --manifest $manifest --npc 0x900 --seed invalid --output $badSeed --json | Out-Null
if ($LASTEXITCODE -ne 2 -or (Test-Path -LiteralPath $badSeed)) { throw 'Preview reroll accepted a malformed seed' }

$emptyManifest = Join-Path $root 'empty-variants.json'
$emptyObject = [ordered]@{ schemaVersion = 1; edition = 'fallout4'; npcFormId = '0x900'; assets = $assets; variants = @() }
[IO.File]::WriteAllText($emptyManifest, ($emptyObject | ConvertTo-Json -Compress -Depth 8), $utf8)
$emptyOutput = Join-Path $root 'empty-output.json'
& $dotnet $cli preview reroll --edition fallout4 --manifest $emptyManifest --npc 0x900 --seed 42 --output $emptyOutput --json | Out-Null
if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $emptyOutput)) { throw 'Preview reroll accepted an empty variant catalog' }

Write-Output "PREVIEW REROLL FIXTURES PASS $root"
