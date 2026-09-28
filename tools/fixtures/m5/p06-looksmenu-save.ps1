$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-looksmenu-save-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_looksmenu_save.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$input = Join-Path $root 'source.json'
$output = Join-Path $root 'canonical.json'
$repeat = Join-Path $root 'canonical-repeat.json'
$response = Join-Path $root 'response.json'
[IO.File]::WriteAllText($input, @'
{
  "BodyMorphs": {"Zeta": 0.2, "Alpha": -0.1},
  "Gender": 1,
  "HairColor": "LooksMenu.esp|0000002A",
  "HeadParts": ["Fallout4.esm|00012345"],
  "Morphs": {"Values": [0.1, 0.2], "Presets": {"00ABCDEF": 0.25}, "Regions": {"00000004": [1.0, -2.0, 3.0]}, "Intensity": 0.75},
  "Overlays": [{"template": "Actors\\Character\\Overlays\\Example.dds", "priority": 3, "tint": [1,0.5,0.25,1], "offsetUV": [0,0], "scaleUV": [1,1]}],
  "Skin": "LooksMenu.esp|00000030",
  "Tints": {"00000002": {"Percent": 50, "Type": 2}, "00000003": {"Color": 3, "ColorID": 33, "Percent": 20, "Type": 1}, "00000001": {"Color": 1, "ColorID": 11, "Percent": 0, "Type": 1}},
  "TintOrder": ["00000003", "00000002", "00000001"],
  "Weight": [0.2, 0.5, 0.3],
  "EngineExtra": {"preserve": "diagnostic-only"}
}
'@, (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli preset export --format looksmenu --edition fallout4 --input $input --output $output --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'LooksMenu canonical export failed' }
& $dotnet $cli preset export --format looksmenu --edition fallout4 --input $input --output $repeat --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'LooksMenu repeat export failed' }
if ((Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $repeat -Algorithm SHA256).Hash) { throw 'LooksMenu export was not deterministic' }

& $dotnet $cli preset inspect --format looksmenu --edition fallout4 --input $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'LooksMenu exported preset did not reload' }
& python $verifier --input $input --output $output --response $response
if ($LASTEXITCODE -ne 0) { throw 'independent LooksMenu save verification failed' }

$incomplete = Join-Path $root 'incomplete.json'
$incompleteOutput = Join-Path $root 'incomplete-output.json'
[IO.File]::WriteAllText($incomplete, '{"Gender":1}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset export --format looksmenu --edition fallout4 --input $incomplete --output $incompleteOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $incompleteOutput)) { throw 'incomplete LooksMenu export was accepted' }

Write-Output "LOOKSMENU SAVE FIXTURES PASS $root"
