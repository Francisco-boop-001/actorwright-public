$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$packFixture = Join-Path $project 'tools\fixtures\m9\p14-facegen-pack.ps1'
$verifier = Join-Path $project 'tools\verification\verify_facegen_deploy.py'
$root = Join-Path $project '03-builds\work\m9-facegen-deploy-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File $packFixture | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The FaceGen pack fixture did not pass.' }

foreach ($item in @(
    @{ Edition = 'fallout4'; Package = Join-Path $project '03-builds\work\m9-facegen-pack-evidence\fallout4' },
    @{ Edition = 'skyrimse'; Package = Join-Path $project '03-builds\work\m9-facegen-pack-evidence\skyrimse' })) {
    $manifest = Join-Path $item.Package 'facegen-pack.json'
    $dataRoot = Join-Path $root "$($item.Edition)\Data"
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $response = Join-Path $root "$($item.Edition)-deployed.json"
    & $dotnet $cli facegen deploy --game $item.Edition --package $manifest --data-root $dataRoot --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "$($item.Edition) FaceGen deployment failed" }
    & python $verifier --manifest $manifest --data-root $dataRoot --edition $item.Edition --mode deployed
    if ($LASTEXITCODE -ne 0) { throw "Independent $($item.Edition) FaceGen deployment verification failed" }

    $idempotentResponse = Join-Path $root "$($item.Edition)-idempotent.json"
    & $dotnet $cli facegen deploy --game $item.Edition --package $manifest --data-root $dataRoot --json |
        Out-File -LiteralPath $idempotentResponse -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not ((Get-Content -Raw -LiteralPath $idempotentResponse | ConvertFrom-Json).alreadyPresent)) {
        throw "$($item.Edition) idempotent FaceGen deployment failed"
    }

    $artifact = Get-Content -Raw -LiteralPath $manifest | ConvertFrom-Json
    $relative = $artifact.files[1].relativePath.Substring(5).Replace('/', '\')
    $conflictPath = Join-Path $dataRoot $relative
    [IO.File]::WriteAllBytes($conflictPath, [byte[]](99, 98, 97))
    $conflictResponse = Join-Path $root "$($item.Edition)-conflict.json"
    $ErrorActionPreference = 'Continue'
    & $dotnet $cli facegen deploy --game $item.Edition --package $manifest --data-root $dataRoot --json |
        Out-File -LiteralPath $conflictResponse -Encoding utf8
    $conflictExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($conflictExit -ne 4) { throw "$($item.Edition) FaceGen destination conflict was not refused" }
    if (-not ((Get-Content -Raw -LiteralPath $conflictResponse).Contains('facegen-deploy-destination-conflict'))) {
        throw "$($item.Edition) FaceGen conflict diagnostic was missing"
    }
    & python $verifier --manifest $manifest --data-root $dataRoot --edition $item.Edition --mode conflict
    if ($LASTEXITCODE -ne 0) { throw "Independent $($item.Edition) FaceGen conflict verification failed" }
}

$unsafeManifest = Join-Path $project '03-builds\work\m9-facegen-pack-evidence\fallout4\facegen-pack.json'
$unsafeResponse = Join-Path $root 'unsafe.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli facegen deploy --game fallout4 --package $unsafeManifest --data-root 'F:\ExampleGame\Data' --json |
    Out-File -LiteralPath $unsafeResponse -Encoding utf8
$unsafeExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($unsafeExit -ne 3) { throw 'Protected live Data root was not refused' }
& python $verifier --manifest $unsafeManifest --data-root 'F:\ExampleGame\Data' --edition fallout4 --mode unsafe
if ($LASTEXITCODE -ne 0) { throw 'Independent unsafe FaceGen deployment verification failed' }

Write-Output "FACEGEN DEPLOY FIXTURE PASS $root (FO4/SSE package promotion, idempotence, conflict, and unsafe-root proof; no live-root write)"
