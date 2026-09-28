$ErrorActionPreference = 'Stop'
$project = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspace = (Resolve-Path (Join-Path $project '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $project 'tools\verification\verify_weight_triangle.py')).Path
$root = Join-Path $project '03-builds\work\m5-weight-triangle-evidence'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Force $root | Out-Null

$normalize = Join-Path $root 'normalize.json'
& $dotnet $cli body weight normalize --game fallout4 --triangle 'thin=2,muscular=-1,fat=1' --json | Out-File -LiteralPath $normalize -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'weight triangle normalize failed' }
python $verifier --operation normalize --triangle 'thin=2,muscular=-1,fat=1' --response $normalize
if ($LASTEXITCODE -ne 0) { throw 'weight triangle normalize independent verification failed' }

$redistribute = Join-Path $root 'redistribute.json'
& $dotnet $cli body weight redistribute --game fallout4 --current 'thin=0.2,muscular=0.3,fat=0.5' --axis thin --value 0.8 --json | Out-File -LiteralPath $redistribute -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'weight triangle redistribute failed' }
python $verifier --operation redistribute --triangle 'thin=0.2,muscular=0.3,fat=0.5' --axis thin --value 0.8 --response $redistribute
if ($LASTEXITCODE -ne 0) { throw 'weight triangle redistribute independent verification failed' }

$negative = Join-Path $root 'negative'; New-Item -ItemType Directory -Force $negative | Out-Null
& $dotnet $cli body weight normalize --game skyrimse --triangle 'thin=1,muscular=0,fat=0' --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'weight triangle accepted Skyrim SE' }
& $dotnet $cli body weight normalize --game fallout4 --triangle 'thin=NaN,muscular=0,fat=0' --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'weight triangle accepted non-finite input' }
& $dotnet $cli body weight normalize --game fallout4 --triangle 'thin=1,muscular=0' --json | Out-Null
if ($LASTEXITCODE -eq 0) { throw 'weight triangle accepted a partial tuple' }

python -m py_compile $verifier
Write-Output "P05 WEIGHT TRIANGLE FIXTURES PASS $root"
