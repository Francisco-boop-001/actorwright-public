$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-racemenu-save-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_racemenu_save.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$input = Join-Path $root 'source.jslot'
$output = Join-Path $root 'canonical.jslot'
$repeat = Join-Path $root 'canonical-repeat.jslot'
$response = Join-Path $root 'response.json'
[IO.File]::WriteAllText($input, @'
{
  "actor": {"hairColor": 1122867, "headTexture": "Skyrim.esm|00000020", "weight": 62},
  "headParts": [{"formId": 74565, "formIdentifier": "Skyrim.esm|00012345", "type": 1}],
  "morphs": {
    "default": {"morphs": [0.1, -0.2, 0.3], "presets": [1, 4294967295]},
    "custom": [{"name": "Smile", "value": 0.2}],
    "sculptDivisor": 10000,
    "sculpt": [{"host": "FemaleHeadCharGen.tri", "vertices": 100, "data": [[3, 100, -200, 300]]}]
  },
  "tintInfo": [],
  "bodyMorphs": [{"name": "Breast", "keys": [{"key": "Base", "value": 0.4}, {"key": "Outfit", "value": 0.2}]}],
  "overrides": [{"node": "Body [Ovl1]", "values": [
    {"key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\tattoo.dds"},
    {"key": 9, "type": 2, "index": 1, "data": "textures\\actors\\character\\tattoo_n.dds"},
    {"key": 7, "type": 3, "index": -1, "data": -16711936},
    {"key": 8, "type": 4, "index": -1, "data": 0.8}
  ]}],
  "transforms": [{"firstPerson": false, "node": "NPC Spine [Spn0]", "keys": [{"name": "RSMTransform", "values": [
    {"key": 30, "type": 4, "index": 2, "data": 1.02}, {"key": 33, "type": 3, "index": 3, "data": 0}
  ]}]}],
  "skinOverrides": [{"firstPerson": false, "slotMask": 32, "values": [
    {"key": 9, "type": 2, "index": 0, "data": "textures\\actors\\character\\body.dds"},
    {"key": 8, "type": 4, "index": -1, "data": 0.75}
  ]}]
}
'@, (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli preset export --format racemenu-jslot --edition skyrimse --input $input --output $output --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'RaceMenu canonical export failed' }
& $dotnet $cli preset export --format racemenu-jslot --edition skyrimse --input $input --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'RaceMenu repeat export failed' }
if ((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $repeat -Algorithm SHA256).Hash) { throw 'RaceMenu export was not deterministic' }

& $dotnet $cli preset inspect --format racemenu-jslot --edition skyrimse --input $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'RaceMenu exported preset did not reload' }
& python $verifier --input $input --output $output --response $response
if ($LASTEXITCODE -ne 0) { throw 'independent RaceMenu save verification failed' }

$incomplete = Join-Path $root 'incomplete.jslot'
$incompleteOutput = Join-Path $root 'incomplete-output.jslot'
[IO.File]::WriteAllText($incomplete, '{"actor":{"weight":101}}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset export --format racemenu-jslot --edition skyrimse --input $incomplete --output $incompleteOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $incompleteOutput)) { throw 'incomplete RaceMenu export was accepted' }

Write-Output "RACEMENU SAVE FIXTURES PASS $root"
