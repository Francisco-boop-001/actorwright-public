$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-racemenu-load-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_racemenu_load.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$input = Join-Path $root 'nested.jslot'
$response = Join-Path $root 'response.json'
[IO.File]::WriteAllText($input, @'
{"headParts":[{"formId":74565,"formIdentifier":"Skyrim.esm|00012345","type":1}],"headTexture":"Skyrim.esm|00000020","actor":{"hairColor":1122867,"weight":62},"morphs":{"default":{"morphs":[0.1,-0.2,0.3],"presets":[1,4294967295],"future":true},"sculptDivisor":10000,"sculpt":[{"host":"FemaleHeadCharGen.tri","vertices":100,"data":[[3,100,-200,300]]}]},"customMorphs":[{"name":"Smile","value":0.2}],"tintInfo":[{"color":4278255360,"index":4,"texture":"textures\\actors\\character\\tint.dds"}],"bodyMorphs":[{"name":"Breast","keys":[{"key":"Base","value":0.4},{"key":"Outfit","value":0.2}]}],"overrides":[{"node":"Body [Ovl1]","diffuse":"textures\\actors\\character\\tattoo.dds","normal":"textures\\actors\\character\\tattoo_n.dds","tint":[1,1,1,1],"alpha":0.8}],"transforms":[{"firstPerson":false,"node":"NPC Spine [Spn0]","keys":[{"name":"RSMTransform","values":[{"key":30,"type":4,"index":2,"data":1.02},{"key":33,"type":3,"index":3,"data":0}]}]}],"skinOverrides":[{"firstPerson":false,"slotMask":32,"values":[{"key":9,"type":2,"index":0,"data":"textures\\actors\\character\\body.dds"},{"key":8,"type":4,"index":-1,"data":0.75}]}],"EngineExtra":{"keep":true}}
'@, (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli preset inspect --format racemenu-jslot --edition skyrimse --input $input --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'RaceMenu nested load fixture failed' }
& python $verifier --input $input --response $response
if ($LASTEXITCODE -ne 0) { throw 'independent RaceMenu load verification failed' }

$invalid = Join-Path $root 'invalid.jslot'
[IO.File]::WriteAllText($invalid, '{"actor":{"weight":101},"overrides":[{"node":"Body","diffuse":"..\\bad.dds"}]}', (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset inspect --format racemenu-jslot --edition skyrimse --input $invalid --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'invalid RaceMenu range/path values were accepted' }

Write-Output "RACEMENU LOAD FIXTURES PASS $root"
