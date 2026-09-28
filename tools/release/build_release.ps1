param(
    [string]$DotNetPath,
    [string]$PackagesPath,
    [string]$RestoreConfigFile,
    [string]$AuditTransportProvenance,
    [string]$OutputRoot,
    [string]$ZipPath,
    [switch]$PrivateNoTag
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$version = '1.0.0-preview.281'
$tag = 'v1.0.0-preview.281'
$releaseName = "actorwright-$version"
if ($AuditTransportProvenance -and -not $RestoreConfigFile) {
    throw 'AuditTransportProvenance requires RestoreConfigFile'
}
if (-not $OutputRoot) { $OutputRoot = Join-Path $projectRoot "artifacts\releases\$releaseName" }
if (-not $ZipPath) { $ZipPath = Join-Path $projectRoot "artifacts\releases\$releaseName.zip" }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$ZipPath = [IO.Path]::GetFullPath($ZipPath)
if (Test-Path -LiteralPath $OutputRoot) { throw "Release output already exists: $OutputRoot" }
if (Test-Path -LiteralPath $ZipPath) { throw "Release ZIP already exists: $ZipPath" }

$git = (Get-Command git.exe -ErrorAction Stop).Source
$commit = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[A-Fa-f0-9]{40}$') { throw 'Unable to resolve release source commit' }
if (-not $PrivateNoTag) {
    & $git -c core.fsmonitor=false -C $projectRoot show-ref --verify --quiet "refs/tags/$tag" 2>$null
    $tagLookupExitCode = $LASTEXITCODE
    if ($tagLookupExitCode -eq 0) { throw "Release tag already exists: $tag" }
    if ($tagLookupExitCode -ne 1) { throw "Unable to determine whether release tag exists: $tag" }
}
$workingTreeStatus = & $git -c core.fsmonitor=false -C $projectRoot status --porcelain
if ($LASTEXITCODE -ne 0 -or $workingTreeStatus) { throw 'Release source has working-tree changes' }
$createdUtc = (& $git -c core.fsmonitor=false -C $projectRoot show -s --format=%cI HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve commit timestamp' }
$createdUtc = ([DateTimeOffset]::Parse($createdUtc)).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')

$dotnetOverride = if ($DotNetPath) {
    (Resolve-Path -LiteralPath $DotNetPath).Path
} else {
    $null
}
$packages = if ($PackagesPath) {
    (Resolve-Path -LiteralPath $PackagesPath).Path
} else {
    [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\nuget-packages'))
}
$python = (Get-Command python.exe -ErrorAction Stop).Source
$buildArguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
    (Join-Path $projectRoot 'tools\build\build.ps1'),
    '-Configuration', 'Release', '-RequireCleanTree', '-NoHttpCache',
    '-SelectorTimeoutSeconds', '600'
)
if ($DotNetPath) {
    $buildArguments += @('-DotNetPath', $dotnetOverride)
}
if ($RestoreConfigFile) {
    $buildArguments += @('-RestoreConfigFile', $RestoreConfigFile)
}
$savedErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $buildOutput = & powershell.exe @buildArguments 2>&1 |
        ForEach-Object { $_.ToString() }
    $buildExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
$buildOutput | Write-Output
if ($buildExitCode -ne 0) { throw "Canonical build failed with exit code $buildExitCode" }
$buildText = ($buildOutput | Out-String).Replace("`r`n", "`n").Replace("`r", "`n")
function Get-BuildEvidenceValue([string]$Name) {
    $matches = [regex]::Matches(
        $buildText,
        "(?m)^$([regex]::Escape($Name))=(?<value>[^`r`n]+)$")
    if ($matches.Count -ne 1) {
        throw "Canonical build output must contain exactly one $Name stamp"
    }
    return $matches[0].Groups['value'].Value
}
$buildSourceCommit = Get-BuildEvidenceValue 'EVIDENCE_SOURCE_COMMIT'
$buildSourceTree = Get-BuildEvidenceValue 'EVIDENCE_SOURCE_TREE'
$buildInitialStatus = Get-BuildEvidenceValue 'EVIDENCE_WORKTREE_STATUS'
$buildDotNetPath = Get-BuildEvidenceValue 'EVIDENCE_DOTNET_PATH'
$buildDotNetSdkVersion = Get-BuildEvidenceValue 'EVIDENCE_DOTNET_SDK_VERSION'
$buildRestoreConfig = Get-BuildEvidenceValue 'EVIDENCE_RESTORE_CONFIG'
$buildRestoreConfigMode = Get-BuildEvidenceValue 'EVIDENCE_RESTORE_CONFIG_MODE'
$buildFinalCommit = Get-BuildEvidenceValue 'EVIDENCE_FINAL_SOURCE_COMMIT'
$buildFinalTree = Get-BuildEvidenceValue 'EVIDENCE_FINAL_SOURCE_TREE'
$buildFinalStatus = Get-BuildEvidenceValue 'EVIDENCE_FINAL_WORKTREE_STATUS'
$buildRestoreConfigHash = Get-BuildEvidenceValue 'EVIDENCE_RESTORE_CONFIG_SHA256'
$buildFinalRestoreConfigHash = Get-BuildEvidenceValue 'EVIDENCE_FINAL_RESTORE_CONFIG_SHA256'
$buildTrackedConfigHash = Get-BuildEvidenceValue 'EVIDENCE_TRACKED_NUGET_CONFIG_SHA256'
$buildNugetAudit = Get-BuildEvidenceValue 'EVIDENCE_NUGET_AUDIT'
$buildHttpCache = Get-BuildEvidenceValue 'EVIDENCE_NUGET_HTTP_CACHE'
$dotnet = (Resolve-Path -LiteralPath $buildDotNetPath).Path
$pinnedSdkVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw |
    ConvertFrom-Json).sdk.version
if ($buildSourceCommit -ne $commit.ToUpperInvariant() -or
    $buildFinalCommit -ne $buildSourceCommit -or
    $buildSourceTree -notmatch '^[A-F0-9]{40}$' -or
    $buildFinalTree -ne $buildSourceTree -or
    $buildInitialStatus -ne 'CLEAN' -or
    $buildDotNetSdkVersion -ne $pinnedSdkVersion -or
    ($dotnetOverride -and $dotnet -ne $dotnetOverride) -or
    [string]::IsNullOrWhiteSpace($buildRestoreConfig) -or
    $buildRestoreConfigMode -notin @('tracked-default', 'explicit-override') -or
    $buildFinalStatus -ne 'CLEAN' -or
    $buildRestoreConfigHash -notmatch '^[A-F0-9]{64}$' -or
    $buildFinalRestoreConfigHash -ne $buildRestoreConfigHash -or
    $buildTrackedConfigHash -notmatch '^[A-F0-9]{64}$' -or
    ($buildRestoreConfigMode -eq 'tracked-default' -and
        $buildRestoreConfigHash -ne $buildTrackedConfigHash) -or
    $buildNugetAudit -match '^(?:false|0|off)$' -or
    $buildHttpCache -ne 'BYPASS') {
    throw 'Canonical build evidence is not publication-grade'
}
$pytestMatch = [regex]::Match(
    $buildText,
    '(?m)^\s*(?<count>[0-9]+) passed(?:,\s*[0-9]+ (?:skipped|warnings?))* in ')
if (-not $pytestMatch.Success) { throw 'Unable to derive the executed standalone Python case count from canonical build output' }
$standalonePythonCases = [int]$pytestMatch.Groups['count'].Value

$staging = Join-Path $projectRoot "artifacts\release-staging\$releaseName-$commit"
if (Test-Path -LiteralPath $staging) { throw "Release staging already exists: $staging" }
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'tools\build\package.ps1') -Configuration Release -Runtime win-x64 -DotNetPath $dotnet -OutputRoot $staging
if ($LASTEXITCODE -ne 0) { throw "Binary package staging failed with exit code $LASTEXITCODE" }
$cli = Join-Path $staging 'cli\actorwright.exe'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "Packaged CLI is missing: $cli"
}

foreach ($name in @('cli', 'desktop', 'schemas', 'docs', 'licenses', 'evidence')) {
    New-Item -ItemType Directory -Path (Join-Path $OutputRoot $name) -ErrorAction Stop | Out-Null
}
$canonicalBuildLog = Join-Path $OutputRoot 'evidence\canonical-build.log'
$canonicalBuildText = $buildText.Replace("`r`n", "`n").Replace("`r", "`n")
[IO.File]::WriteAllText($canonicalBuildLog, $canonicalBuildText, (New-Object Text.UTF8Encoding($false)))
New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'schemas\exchange\v1'), (Join-Path $OutputRoot 'schemas\exchange\v2') -ErrorAction Stop | Out-Null
Copy-Item -LiteralPath (Join-Path $staging 'cli\actorwright.exe') -Destination (Join-Path $OutputRoot 'cli\actorwright.exe') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $staging 'desktop\Actorwright.Desktop.exe') -Destination (Join-Path $OutputRoot 'desktop\Actorwright.Desktop.exe') -ErrorAction Stop
@('@echo off', '"%~dp0actorwright.exe" %*') | Set-Content -LiteralPath (Join-Path $OutputRoot 'cli\npcm.cmd') -Encoding ASCII
$launcherSource = Join-Path $projectRoot 'tools\build\actorwright.ps1'
Copy-Item -LiteralPath $launcherSource -Destination (Join-Path $OutputRoot 'cli\actorwright.ps1') -ErrorAction Stop
$utf8NoBom = New-Object Text.UTF8Encoding($false)
foreach ($exchangeVersion in @('v1', 'v2')) {
    $sourceSchemaRoot = Join-Path $projectRoot "contracts\exchange\$exchangeVersion"
    $targetSchemaRoot = Join-Path $OutputRoot "schemas\exchange\$exchangeVersion"
    Get-ChildItem -LiteralPath $sourceSchemaRoot -Filter '*.schema.json' -File | ForEach-Object {
        $schemaText = [IO.File]::ReadAllText($_.FullName)
        $schemaText = $schemaText.Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText((Join-Path $targetSchemaRoot $_.Name), $schemaText, $utf8NoBom)
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $OutputRoot 'docs\README.md') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\releases\1.0.0-preview.281.md') -Destination (Join-Path $OutputRoot 'docs\1.0.0-preview.281.md') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\alpha-consumer-handoff.md') -Destination (Join-Path $OutputRoot 'docs\alpha-consumer-handoff.md') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\external-rendering-prerequisites.md') -Destination (Join-Path $OutputRoot 'docs\external-rendering-prerequisites.md') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\licenses.md') -Destination (Join-Path $OutputRoot 'docs\licenses.md') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\third-party-licenses.md') -Destination (Join-Path $OutputRoot 'docs\third-party-licenses.md') -ErrorAction Stop
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'licenses') -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $OutputRoot 'licenses') -Recurse -Force -ErrorAction Stop
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $OutputRoot 'licenses\LICENSE') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $projectRoot 'NOTICE') -Destination (Join-Path $OutputRoot 'licenses\NOTICE') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $staging 'capabilities.json') -Destination (Join-Path $OutputRoot 'evidence\capabilities.json') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $staging 'cli-resource-closure.json') -Destination (Join-Path $OutputRoot 'evidence\cli-resource-closure.json') -ErrorAction Stop
Copy-Item -LiteralPath (Join-Path $staging 'desktop-resource-closure.json') -Destination (Join-Path $OutputRoot 'evidence\desktop-resource-closure.json') -ErrorAction Stop
$env:ACTORWRIGHT_WORKSPACE_ROOT = $projectRoot
$protocolCapabilitiesOutput = & $cli capabilities --protocol 2 --json
if ($LASTEXITCODE -ne 0) {
    throw "Packaged protocol-v2 capabilities failed with exit code $LASTEXITCODE"
}
$protocolCapabilities = $protocolCapabilitiesOutput | ConvertFrom-Json
$readyCommands = @($protocolCapabilities.result.commands |
    Where-Object { $_.readiness -eq 'v2' } |
    Sort-Object -Property name)
$expectedProtocolV2ReadyCommands = @(
    'capabilities',
    'npc assembly preflight',
    'npc create-from-jslot',
    'npc finish analyze',
    'npc finish apply',
    'npc finish verify',
    'preset inspect',
    'preview npc',
    'schema export',
    'version',
    'workspace preflight'
)
$actualProtocolV2ReadyCommands = @($readyCommands | ForEach-Object { $_.name })
if ($protocolCapabilities.outcome -ne 'succeeded' -or
    $protocolCapabilities.result.protocolVersion -ne '2' -or
    ($actualProtocolV2ReadyCommands -join "`n") -ne
        ($expectedProtocolV2ReadyCommands -join "`n")) {
    throw 'Packaged protocol-v2 capabilities are not a truthful ready-command catalogue.'
}
$protocolCapabilities | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath `
    (Join-Path $OutputRoot 'evidence\protocol-v2-capabilities.json') `
    -Encoding UTF8
$schemaRows = @()
foreach ($readyCommand in $readyCommands) {
    $schemaOutput = & $cli schema export --protocol 2 --json `
        --command $readyCommand.name
    if ($LASTEXITCODE -ne 0) {
        throw "Packaged schema export failed for '$($readyCommand.name)' with exit code $LASTEXITCODE"
    }
    $schemaEnvelope = $schemaOutput | ConvertFrom-Json
    if ($schemaEnvelope.outcome -ne 'succeeded' -or
        $schemaEnvelope.result.protocolVersion -ne '2' -or
        $schemaEnvelope.result.contract.name -ne $readyCommand.name -or
        $schemaEnvelope.result.contract.readiness -ne 'v2' -or
        (($schemaEnvelope.result.contract.resultSchemaIds | ConvertTo-Json -Compress) -ne
         ($readyCommand.resultSchemaIds | ConvertTo-Json -Compress))) {
        throw "Packaged schema export drifted from capabilities for '$($readyCommand.name)'."
    }
    $schemaRows += [ordered]@{
        command = $readyCommand.name
        resultSchemaIds = @($readyCommand.resultSchemaIds)
        export = $schemaEnvelope.result
    }
}
[ordered]@{
    schemaVersion = 1
    productVersion = $version
    sourceLine = 'preview.281-public'
    protocolVersion = '2'
    commands = @($schemaRows)
} | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath `
    (Join-Path $OutputRoot 'evidence\protocol-v2-schema-exports.json') `
    -Encoding UTF8
$packageManifest = Get-Content -LiteralPath (Join-Path $staging 'manifest.json') -Raw | ConvertFrom-Json
if ($packageManifest.embeddedResourceClosures.Count -ne 2) {
    throw 'Binary package did not bind both executable resource closures.'
}

$vulnerabilityReport = Join-Path $OutputRoot 'evidence\dependency-vulnerability-report.json'
if (-not (Test-Path -LiteralPath $packages -PathType Container)) {
    throw "Dependency package cache is missing after canonical build: $packages"
}
$transportArguments = @()
if ($RestoreConfigFile) {
    $transportArguments += @('-RestoreConfigFile', $RestoreConfigFile)
}
if ($AuditTransportProvenance) {
    $transportArguments += @(
        '-AuditTransportProvenance', $AuditTransportProvenance)
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
    (Join-Path $projectRoot 'tools\hardening\dependency_vulnerability_scan.ps1') `
    -DotNetPath $dotnet -PackagesPath $packages -Output $vulnerabilityReport `
    -AdvisoryCoverage public @transportArguments
if ($LASTEXITCODE -ne 0) {
    throw "Dependency vulnerability scan failed with exit code $LASTEXITCODE"
}
& $python (Join-Path $projectRoot 'tools\hardening\validate_dependency_vulnerability_report.py') `
    $vulnerabilityReport --public-release
if ($LASTEXITCODE -ne 0) {
    throw "Dependency vulnerability report validation failed with exit code $LASTEXITCODE"
}

Push-Location $projectRoot
try {
    $derivedPinJson = & $python -m tools.release.derive_build_pins `
        $canonicalBuildLog `
        (Join-Path $OutputRoot 'evidence\protocol-v2-capabilities.json')
    $derivePinExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($derivePinExitCode -ne 0) {
    throw "Release overlap pin derivation failed with exit code $derivePinExitCode"
}
$derivedPins = ($derivedPinJson -join "`n") | ConvertFrom-Json
if ($derivedPins.standalonePythonCases -ne $standalonePythonCases) {
    throw 'Release Python pass count disagrees with canonical build output'
}

$testSummary = [ordered]@{
    schemaVersion = 1
    status = 'PASS'
    sourceCommit = $commit
    sourceTag = $tag
    configuration = 'Release'
    projectsCompiled = 25
    warnings = 0
    errors = 0
    exactCommandNames = 142
    standalonePythonCases = $standalonePythonCases
    standalonePythonSkipped = $derivedPins.standalonePythonSkipped
    orderedHelpSha256 = $derivedPins.orderedHelpSha256
    protocolReadinessSha256 = $derivedPins.protocolReadinessSha256
    selectorInventorySha256 = $derivedPins.selectorInventorySha256
    selectorResultsSha256 = $derivedPins.selectorResultsSha256
    legacyWorkspaceBoundSuites = 'COMPILE_ONLY'
    runtimeAuthority = $false
    visualAuthority = $false
}
$testSummary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'evidence\test-summary.json') -Encoding UTF8

$rendererRows = @()
foreach ($script in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'runtime\rendering') -Filter '*.py' -File | Sort-Object Name) {
    $rendererRows += [ordered]@{ logicalName = "Actorwright.Rendering.Scripts.$($script.Name)"; sha256 = (Get-FileHash -LiteralPath $script.FullName -Algorithm SHA256).Hash; size = $script.Length }
}
if ($rendererRows.Count -ne 6) { throw "Expected six embedded renderer sources, got $($rendererRows.Count)" }
[ordered]@{ schemaVersion = 1; embedded = 6; loosePython = 0; scripts = $rendererRows } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'evidence\renderer-scripts.json') -Encoding UTF8

& $python (Join-Path $projectRoot 'tools\release\generate_sbom.py') --root $projectRoot --output (Join-Path $OutputRoot 'evidence\sbom.spdx.json') --version $version --commit $commit --created-utc $createdUtc
if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed with exit code $LASTEXITCODE" }

$postAssemblyCommit = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse HEAD).Trim()
$postAssemblyTree = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse 'HEAD^{tree}').Trim().ToUpperInvariant()
$postAssemblyStatus = @(& $git -c core.fsmonitor=false -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or
    $postAssemblyCommit -ne $commit -or
    $postAssemblyTree -ne $buildSourceTree -or
    $postAssemblyStatus.Count -ne 0) {
    throw 'Release source identity or cleanliness changed during package assembly'
}

$release = [ordered]@{
    schemaVersion = 2
    product = 'Actorwright'
    version = $version
    sourceCommit = $commit
    sourceTree = $buildSourceTree
    sourceTag = $tag
    privateOnly = [bool]$PrivateNoTag
    runtimeAuthority = $false
    visualAuthority = $false
    capabilitiesSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'evidence\capabilities.json') -Algorithm SHA256).Hash
    sbomSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'evidence\sbom.spdx.json') -Algorithm SHA256).Hash
    testSummarySha256 = (Get-FileHash -LiteralPath (Join-Path $OutputRoot 'evidence\test-summary.json') -Algorithm SHA256).Hash
    dependencyVulnerabilityReportSha256 = (Get-FileHash -LiteralPath $vulnerabilityReport -Algorithm SHA256).Hash
    canonicalBuildLogSha256 = (Get-FileHash -LiteralPath $canonicalBuildLog -Algorithm SHA256).Hash
    embeddedResourceClosures = @($packageManifest.embeddedResourceClosures)
}
$release | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputRoot 'actorwright-release.json') -Encoding UTF8

$filesByRelative = @{}
foreach ($file in Get-ChildItem -LiteralPath $OutputRoot -Recurse -File) {
    $relative = $file.FullName.Substring($OutputRoot.Length + 1).Replace('\', '/')
    $filesByRelative.Add($relative, $file.FullName)
}
$relativePaths = [string[]]$filesByRelative.Keys
[Array]::Sort($relativePaths, [StringComparer]::Ordinal)
$hashLines = foreach ($relative in $relativePaths) {
    "$(Get-FileHash -LiteralPath $filesByRelative[$relative] -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relative"
}
$hashLines | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS') -Encoding ASCII

if ($PrivateNoTag) {
    $savedErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $verificationOutput = @(& $python (Join-Path $projectRoot 'tools\release\verify_release.py') `
            $OutputRoot --create-zip $ZipPath --json 2>&1 |
            ForEach-Object { $_.ToString() })
        $verificationExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    if ($verificationExitCode -ne 0) {
        throw "Release verification failed with exit code $verificationExitCode"
    }
    try {
        $rawVerificationJson = [string]$verificationOutput[-1]
        if ([type]::GetType('System.Text.Json.JsonDocument, System.Text.Json')) {
            $document = [System.Text.Json.JsonDocument]::Parse($rawVerificationJson)
            try {
                if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) {
                    throw 'Verifier JSON root must be an object'
                }
            } finally {
                $document.Dispose()
            }
        } else {
            Add-Type -AssemblyName System.Web.Extensions -ErrorAction Stop
            $jsonParser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
            if ($jsonParser.DeserializeObject($rawVerificationJson) -isnot [System.Collections.IDictionary]) {
                throw 'Verifier JSON root must be an object'
            }
        }
        $verificationResult = $rawVerificationJson | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw 'Release verification did not emit a valid sealed compatibility result'
    }
    if ($verificationResult -isnot [pscustomobject] -or
        $verificationResult.status -isnot [string] -or
        $verificationResult.status -cne 'PASS' -or
        $verificationResult.zipVerified -isnot [bool] -or
        $verificationResult.zipVerified -ne $true -or
        $verificationResult.compatibilityVerdict -isnot [string] -or
        $verificationResult.compatibilityVerdict -cne 'FULL_COMPATIBLE' -or
        $verificationResult.sourceCommit -isnot [string] -or
        $verificationResult.sourceCommit -ine $commit -or
        $verificationResult.version -isnot [string] -or
        $verificationResult.version -cne $version) {
        throw 'Release verification did not establish FULL_COMPATIBLE sealed bytes for this source'
    }
    $verificationOutput | Write-Output
} else {
    & (Join-Path $projectRoot 'tools\release\verify_and_tag.ps1') `
        -ProjectRoot $projectRoot -PythonPath $python -GitPath $git `
        -OutputRoot $OutputRoot -ZipPath $ZipPath -Tag $tag -Commit $commit `
        -Version $version
}
Write-Output "Actorwright binary-only release: PASS $OutputRoot"
Write-Output "Actorwright deterministic ZIP: PASS $ZipPath"
