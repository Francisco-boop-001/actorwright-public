$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\hardening\verify_runtime_smoke.py'
$acceptance = Join-Path $project '05-reports\m8-package-acceptance-2026-07-18bx.json'
$root = Join-Path $project '03-builds\work\m9-runtime-smoke-validator'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$environment = Join-Path $root 'environment.json'
Set-Content -LiteralPath $environment -Value '{"profile":"synthetic-validator-fixture"}' -Encoding utf8
$environmentHash = (Get-FileHash -LiteralPath $environment -Algorithm SHA256).Hash.ToLowerInvariant()
$archiveHash = (Get-Content -LiteralPath $acceptance -Raw | ConvertFrom-Json).packageArchiveSha256
foreach ($name in @('face','neck','body','hands','eyes','hair','outfit')) {
    [System.IO.File]::WriteAllBytes((Join-Path $root "$name.png"), [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
}

function New-Report([string] $game, [string[]] $views) {
    $screenshots = @{}
    foreach ($view in $views) { $screenshots[$view] = Join-Path $root "$view.png" }
    [ordered]@{
        schemaVersion = '1'
        game = $game
        status = 'PASS'
        controlNpcSameFrame = $true
        providerMatchesPackage = $true
        package = [ordered]@{ archiveSha256 = $archiveHash }
        target = [ordered]@{
            formId = '0x800'; plugin = 'SyntheticFixture.esp'
            faceGeomProvider = 'synthetic-facegeom'; faceTintProvider = 'synthetic-facetint'
            bodySkinProvider = 'synthetic-skin'; headpartProviders = @('synthetic-hair')
            outfitProvider = 'synthetic-outfit'
        }
        control = [ordered]@{ formId = '0x801'; plugin = 'SyntheticControl.esp' }
        environmentFingerprint = [ordered]@{ path = $environment; sha256 = $environmentHash }
        screenshots = $screenshots
        operator = [ordered]@{ name = 'synthetic-validator-fixture'; capturedAt = '2026-07-18T00:00:00Z' }
    } | ConvertTo-Json -Depth 8
}

$fo4 = Join-Path $root 'fallout4.json'
$sse = Join-Path $root 'skyrimse.json'
New-Report 'fallout4' @('face','neck','body','hands','eyes','outfit') | Set-Content -LiteralPath $fo4 -Encoding utf8
New-Report 'skyrimse' @('face','neck','body','hands','eyes','hair','outfit') | Set-Content -LiteralPath $sse -Encoding utf8

foreach ($item in @(@('fallout4', $fo4), @('skyrimse', $sse))) {
    & $dotnet $cli runtime smoke verify --game $item[0] --runtime-report $item[1] --package-acceptance $acceptance --json |
        Out-File -LiteralPath (Join-Path $root "$($item[0])-cli.json") -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Typed runtime smoke validation failed for $($item[0])" }
}

& python $verifier --fallout4 $fo4 --skyrimse $sse --package-acceptance $acceptance --output (Join-Path $root 'independent.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent runtime smoke verification failed' }

$failedAcceptance = Join-Path $root 'failed-acceptance.json'
$failedAcceptanceDocument = Get-Content -LiteralPath $acceptance -Raw | ConvertFrom-Json
$failedAcceptanceDocument.status = 'FAIL'
$failedAcceptanceDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $failedAcceptance -Encoding utf8
$failedAcceptanceOutput = Join-Path $root 'independent-failed-acceptance.json'
& python $verifier --fallout4 $fo4 --skyrimse $sse --package-acceptance $failedAcceptance --output $failedAcceptanceOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted a failed package acceptance report' }

$sameIdentityReport = Join-Path $root 'fallout4-same-control.json'
$sameIdentityDocument = Get-Content -LiteralPath $fo4 -Raw | ConvertFrom-Json
$sameIdentityDocument.control.formId = $sameIdentityDocument.target.formId
$sameIdentityDocument.control.plugin = $sameIdentityDocument.target.plugin
$sameIdentityDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $sameIdentityReport -Encoding utf8
$sameIdentityOutput = Join-Path $root 'independent-same-control.json'
& python $verifier --fallout4 $sameIdentityReport --skyrimse $sse --package-acceptance $acceptance --output $sameIdentityOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted the target NPC as its own control NPC' }

$invalidSchemaReport = Join-Path $root 'fallout4-invalid-types.json'
$invalidSchemaDocument = Get-Content -LiteralPath $fo4 -Raw | ConvertFrom-Json
$invalidSchemaDocument.target.formId = @{ value = '0x800' }
$invalidSchemaDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $invalidSchemaReport -Encoding utf8
$invalidSchemaOutput = Join-Path $root 'independent-invalid-types.json'
& python $verifier --fallout4 $invalidSchemaReport --skyrimse $sse --package-acceptance $acceptance --output $invalidSchemaOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted a malformed typed identity field' }

$invalidScreenshot = Join-Path $root 'not-an-image.bin'
[System.IO.File]::WriteAllBytes($invalidScreenshot, [byte[]](1, 2, 3))
$invalidReport = Join-Path $root 'fallout4-invalid-image.json'
$invalidDocument = Get-Content -LiteralPath $fo4 -Raw | ConvertFrom-Json
$invalidDocument.screenshots.face = $invalidScreenshot
$invalidDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $invalidReport -Encoding utf8
$invalidOutput = Join-Path $root 'independent-invalid-image.json'
& python $verifier --fallout4 $invalidReport --skyrimse $sse --package-acceptance $acceptance --output $invalidOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted a non-image screenshot file' }

$oversizedScreenshot = Join-Path $root 'oversized.png'
$oversizedStream = [System.IO.File]::Open($oversizedScreenshot, [System.IO.FileMode]::CreateNew,
    [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    $oversizedStream.SetLength((64 * 1024 * 1024) + 1)
    $pngHeader = [Convert]::FromBase64String('iVBORw0KGgo=')
    $oversizedStream.Position = 0
    $oversizedStream.Write($pngHeader, 0, $pngHeader.Length)
}
finally { $oversizedStream.Dispose() }
$oversizedReport = Join-Path $root 'fallout4-oversized-image.json'
$oversizedDocument = Get-Content -LiteralPath $fo4 -Raw | ConvertFrom-Json
$oversizedDocument.screenshots.face = $oversizedScreenshot
$oversizedDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $oversizedReport -Encoding utf8
$oversizedOutput = Join-Path $root 'independent-oversized-image.json'
& python $verifier --fallout4 $oversizedReport --skyrimse $sse --package-acceptance $acceptance --output $oversizedOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted an oversized screenshot file' }

$oversizedJson = Join-Path $root 'fallout4-oversized-report.json'
$reportText = Get-Content -LiteralPath $fo4 -Raw
$paddingLength = (2 * 1024 * 1024) + 1 - [System.Text.Encoding]::UTF8.GetByteCount($reportText)
$reportBuilder = [System.Text.StringBuilder]::new($reportText)
if ($paddingLength -gt 0) { [void]$reportBuilder.Append(' ', $paddingLength) }
[System.IO.File]::WriteAllText($oversizedJson, $reportBuilder.ToString(), [System.Text.UTF8Encoding]::new($false))
$oversizedJsonOutput = Join-Path $root 'independent-oversized-report.json'
& python $verifier --fallout4 $oversizedJson --skyrimse $sse --package-acceptance $acceptance --output $oversizedJsonOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted an oversized report file' }

$outsideOutput = 'F:\ExampleGame\runtime-smoke-verification.json'
& python $verifier --fallout4 $fo4 --skyrimse $sse --package-acceptance $acceptance --output $outsideOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted an output path outside the K-local project root' }

$alternateStreamOutput = Join-Path $root 'independent.json:stream'
& python $verifier --fallout4 $fo4 --skyrimse $sse --package-acceptance $acceptance --output $alternateStreamOutput
if ($LASTEXITCODE -eq 0) { throw 'Independent runtime smoke verifier accepted an alternate-data-stream output path' }

$unsafe = Join-Path $root 'unsafe.json'
$ErrorActionPreference = 'Continue'
& $dotnet $cli runtime smoke verify --game fallout4 --runtime-report 'F:\ExampleGame\runtime.json' --package-acceptance $acceptance --json |
    Out-File -LiteralPath $unsafe -Encoding utf8
$unsafeExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($unsafeExit -ne 3) { throw 'Unsafe runtime report was not refused' }

Write-Output "RUNTIME SMOKE VALIDATOR FIXTURE PASS $root (synthetic schema/safety only; no runtime or pixel claim)"
