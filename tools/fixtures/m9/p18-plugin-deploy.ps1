$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_plugin_deploy.py'
$root = Join-Path $project '03-builds\work\m9-plugin-deploy'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$sources = @(
    @{ Edition = 'fallout4'; Source = Join-Path $project '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp' },
    @{ Edition = 'skyrimse'; Source = Join-Path $project '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp' }
)
foreach ($item in $sources) {
    $hash = (Get-FileHash -LiteralPath $item.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    $dataRoot = Join-Path $root "$($item.Edition)\Data"
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $deployedResponse = Join-Path $root "$($item.Edition)-deployed.json"
    & $dotnet $cli plugin deploy --game $item.Edition --plugin $item.Source --data-root $dataRoot --expected-sha256 $hash --json |
        Out-File -LiteralPath $deployedResponse -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "$($item.Edition) plugin deployment failed" }
    $destination = Join-Path $dataRoot ([IO.Path]::GetFileName($item.Source))
    & python $verifier --source $item.Source --destination $destination --expected-sha256 $hash --mode deployed
    if ($LASTEXITCODE -ne 0) { throw "Independent $($item.Edition) deployment verification failed" }

    $idempotentResponse = Join-Path $root "$($item.Edition)-idempotent.json"
    & $dotnet $cli plugin deploy --game $item.Edition --plugin $item.Source --data-root $dataRoot --expected-sha256 $hash --json |
        Out-File -LiteralPath $idempotentResponse -Encoding utf8
    if ($LASTEXITCODE -ne 0 -or -not ((Get-Content -Raw -LiteralPath $idempotentResponse | ConvertFrom-Json).alreadyPresent)) {
        throw "$($item.Edition) idempotent deployment failed"
    }

    [IO.File]::WriteAllBytes($destination, [byte[]](1, 2, 3))
    $conflictResponse = Join-Path $root "$($item.Edition)-conflict.json"
    $ErrorActionPreference = 'Continue'
    & $dotnet $cli plugin deploy --game $item.Edition --plugin $item.Source --data-root $dataRoot --expected-sha256 $hash --json |
        Out-File -LiteralPath $conflictResponse -Encoding utf8
    $conflictExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($conflictExit -ne 4) { throw "$($item.Edition) conflicting deployment was not refused" }
    if (-not ((Get-Content -Raw -LiteralPath $conflictResponse).Contains('plugin-deploy-destination-conflict'))) {
        throw "$($item.Edition) conflict diagnostic was missing"
    }
    & python $verifier --source $item.Source --destination $destination --expected-sha256 $hash --mode conflict
    if ($LASTEXITCODE -ne 0) { throw "Independent $($item.Edition) conflict verification failed" }
}

$unsafeResponse = Join-Path $root 'unsafe.json'
$unsafeSource = $sources[0].Source
$unsafeHash = (Get-FileHash -LiteralPath $unsafeSource -Algorithm SHA256).Hash.ToLowerInvariant()
$ErrorActionPreference = 'Continue'
& $dotnet $cli plugin deploy --game fallout4 --plugin $unsafeSource --data-root 'F:\ExampleGame\Data' --expected-sha256 $unsafeHash --json |
    Out-File -LiteralPath $unsafeResponse -Encoding utf8
$unsafeExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($unsafeExit -ne 3) { throw 'Protected live Data root was not refused' }
& python $verifier --source $unsafeSource --destination 'F:\ExampleGame\Data\M2FixtureFO4.esp' --expected-sha256 $unsafeHash --mode unsafe
if ($LASTEXITCODE -ne 0) { throw 'Independent unsafe deployment verification failed' }

Write-Output "PLUGIN DEPLOY FIXTURE PASS $root (dual-edition deployed/idempotent/conflict/unsafe evidence; no live-root write)"
