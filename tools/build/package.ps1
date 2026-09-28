param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$DotNetPath,
    [string]$PythonPath,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $projectRoot 'artifacts\packages\actorwright-1.0.0-preview.281-win-x64'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Package output already exists: $OutputRoot"
}
if ($DotNetPath) {
    $dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
if ($PythonPath) {
    $python = (Resolve-Path -LiteralPath $PythonPath).Path
} else {
    $python = (Get-Command python -ErrorAction Stop).Source
}
$env:ACTORWRIGHT_TEST_DOTNET = $dotnet

$artifacts = Join-Path $projectRoot 'artifacts'
$env:DOTNET_CLI_HOME = Join-Path $artifacts 'dotnet-home'
if (-not $env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES = Join-Path $artifacts 'nuget-packages'
}
$env:NuGetAudit = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$cliOutput = Join-Path $OutputRoot 'cli'
$desktopOutput = Join-Path $OutputRoot 'desktop'
$exchangeSchemaV1Output = Join-Path $OutputRoot 'schemas\exchange\v1'
$exchangeSchemaV2Output = Join-Path $OutputRoot 'schemas\exchange\v2'
New-Item -ItemType Directory -Path $cliOutput, $desktopOutput, $exchangeSchemaV1Output, $exchangeSchemaV2Output | Out-Null

$publishProperties = @(
    '-p:PublishSingleFile=true',
    '-p:SelfContained=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:IncludeAllContentForSelfExtract=true',
    '-p:CopyDebugSymbolFilesFromPackages=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)

& $dotnet publish (Join-Path $projectRoot 'src\NpcManager.Cli\NpcManager.Cli.csproj') `
    --configuration $Configuration --runtime $Runtime --self-contained true `
    --no-restore --output $cliOutput @publishProperties
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }
& $dotnet publish (Join-Path $projectRoot 'src\NpcManager.Desktop\NpcManager.Desktop.csproj') `
    --configuration $Configuration --runtime $Runtime --self-contained true `
    --no-restore --output $desktopOutput @publishProperties
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed with exit code $LASTEXITCODE" }

$cli = Join-Path $cliOutput 'actorwright.exe'
$desktop = Join-Path $desktopOutput 'Actorwright.Desktop.exe'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf) -or
    -not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
    throw 'Expected Actorwright CLI and desktop executables were not published.'
}
$launcherSource = Join-Path $PSScriptRoot 'actorwright.ps1'
$launcher = Join-Path $cliOutput 'actorwright.ps1'
Copy-Item -LiteralPath $launcherSource -Destination $launcher -ErrorAction Stop
foreach ($debugSymbol in @(
    (Join-Path $cliOutput 'libSkiaSharp.pdb'),
    (Join-Path $desktopOutput 'libSkiaSharp.pdb')
)) {
    if (Test-Path -LiteralPath $debugSymbol -PathType Leaf) {
        $item = Get-Item -LiteralPath $debugSymbol
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $item.Name -ne 'libSkiaSharp.pdb') {
            throw "Refusing unexpected debug-symbol cleanup target: $debugSymbol"
        }
        Remove-Item -LiteralPath $debugSymbol -Force
    }
}
$entrypointFiles = Get-ChildItem -LiteralPath $cliOutput, $desktopOutput -File -Recurse
if ($entrypointFiles.Count -ne 3 -or
    $entrypointFiles.FullName -notcontains $cli -or
    $entrypointFiles.FullName -notcontains $launcher -or
    $entrypointFiles.FullName -notcontains $desktop) {
    throw 'Packaged entry points must contain only the expected executables and launcher.'
}
$unexpected = Get-ChildItem -LiteralPath $cliOutput, $desktopOutput -File -Recurse |
    Where-Object { $_.Extension -eq '.py' }
if ($unexpected) { throw 'Published package contains loose Python renderer source.' }

# Keep the binary staging inventory self-contained: the Exchange v1 envelope
# schemas and the v2 response contract are shipped as exact UTF-8/LF files.
foreach ($exchangeVersion in @('v1', 'v2')) {
    $sourceSchemaRoot = Join-Path $projectRoot "contracts\exchange\$exchangeVersion"
    $targetSchemaRoot = Join-Path $OutputRoot "schemas\exchange\$exchangeVersion"
    Get-ChildItem -LiteralPath $sourceSchemaRoot -Filter '*.schema.json' -File | ForEach-Object {
        $schemaText = [IO.File]::ReadAllText($_.FullName)
        $schemaText = $schemaText.Replace("`r`n", "`n").Replace("`r", "`n")
        [IO.File]::WriteAllText((Join-Path $targetSchemaRoot $_.Name), $schemaText, (New-Object Text.UTF8Encoding($false)))
    }
}

$capabilitiesPath = Join-Path $OutputRoot 'capabilities.json'
$env:ACTORWRIGHT_WORKSPACE_ROOT = $projectRoot
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $artifacts 'bundle-extract'
New-Item -ItemType Directory -Force -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR | Out-Null
& $cli capabilities --json | Set-Content -LiteralPath $capabilitiesPath -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw "packaged CLI capabilities failed with exit code $LASTEXITCODE" }
$capabilities = Get-Content -LiteralPath $capabilitiesPath -Raw | ConvertFrom-Json
if ($capabilities.commands.Count -ne 142 -or
    $capabilities.sourceLine -ne 'preview.281-public' -or
    $capabilities.version -ne '1.0.0-preview.281') {
    throw 'Packaged CLI identity or 142-command catalogue is incorrect.'
}

$consumerSmokeWorkspace = Join-Path $artifacts 'consumer-smoke-workspace'
$consumerSmokeExtract = Join-Path $artifacts 'consumer-smoke-bundle-extract'
New-Item -ItemType Directory -Force -Path $consumerSmokeWorkspace, $consumerSmokeExtract | Out-Null
$env:ACTORWRIGHT_WORKSPACE_ROOT = $consumerSmokeWorkspace
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $consumerSmokeExtract
$consumerCapabilities = & $cli capabilities --json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or
    $consumerCapabilities.commands.Count -ne 142 -or
    $consumerCapabilities.sourceLine -ne 'preview.281-public' -or
    $consumerCapabilities.version -ne '1.0.0-preview.281') {
    throw "packaged CLI consumer-style startup failed with exit code $LASTEXITCODE"
}

$probeExtractRoot = Join-Path $artifacts 'x'
New-Item -ItemType Directory -Force -Path $probeExtractRoot | Out-Null
$probeId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$cliProbeExtract = Join-Path $probeExtractRoot "c-$probeId"
$desktopProbeExtract = Join-Path $probeExtractRoot "d-$probeId"
New-Item -ItemType Directory -Path $cliProbeExtract, $desktopProbeExtract | Out-Null
$cliProbePath = Join-Path $OutputRoot 'cli-resource-closure.json'
$desktopProbePath = Join-Path $OutputRoot 'desktop-resource-closure.json'
$env:ACTORWRIGHT_WORKSPACE_ROOT = $consumerSmokeWorkspace
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $cliProbeExtract
& $cli --resource-extraction-probe | Set-Content -LiteralPath $cliProbePath -Encoding UTF8
if ($LASTEXITCODE -ne 0) { throw "packaged CLI resource probe failed with exit code $LASTEXITCODE" }
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $desktopProbeExtract
$desktopProbeStart = New-Object Diagnostics.ProcessStartInfo
$desktopProbeStart.FileName = $desktop
$desktopProbeStart.Arguments = '--resource-extraction-probe'
$desktopProbeStart.UseShellExecute = $false
$desktopProbeStart.CreateNoWindow = $true
$desktopProbeStart.RedirectStandardOutput = $true
$desktopProbeStart.RedirectStandardError = $true
$desktopProbeProcess = New-Object Diagnostics.Process
$desktopProbeProcess.StartInfo = $desktopProbeStart
if (-not $desktopProbeProcess.Start()) { throw 'Packaged desktop resource probe did not start.' }
$desktopProbeOutput = $desktopProbeProcess.StandardOutput.ReadToEnd()
$desktopProbeError = $desktopProbeProcess.StandardError.ReadToEnd()
$desktopProbeProcess.WaitForExit()
if ($desktopProbeProcess.ExitCode -ne 0) {
    throw "packaged desktop resource probe failed with exit code $($desktopProbeProcess.ExitCode): $desktopProbeError"
}
[IO.File]::WriteAllText($desktopProbePath, $desktopProbeOutput, (New-Object Text.UTF8Encoding($false)))
$cliProbe = Get-Content -LiteralPath $cliProbePath -Raw | ConvertFrom-Json
$desktopProbe = Get-Content -LiteralPath $desktopProbePath -Raw | ConvertFrom-Json
if (-not $cliProbe.accepted -or -not $desktopProbe.accepted -or
    $cliProbe.runtimeManifestSha256 -ne $desktopProbe.runtimeManifestSha256 -or
    ($cliProbe.entries | ConvertTo-Json -Depth 8 -Compress) -ne
        ($desktopProbe.entries | ConvertTo-Json -Depth 8 -Compress)) {
    throw 'Packaged CLI and desktop embedded resource closures are not identical and admitted.'
}
$embeddedResourceClosures = @(
    [ordered]@{
        entrypoint = 'cli/actorwright.exe'
        probeSha256 = (Get-FileHash -LiteralPath $cliProbePath -Algorithm SHA256).Hash.ToLowerInvariant()
        runtimeManifestSha256 = $cliProbe.runtimeManifestSha256.ToLowerInvariant()
        entries = @($cliProbe.entries)
    },
    [ordered]@{
        entrypoint = 'desktop/Actorwright.Desktop.exe'
        probeSha256 = (Get-FileHash -LiteralPath $desktopProbePath -Algorithm SHA256).Hash.ToLowerInvariant()
        runtimeManifestSha256 = $desktopProbe.runtimeManifestSha256.ToLowerInvariant()
        entries = @($desktopProbe.entries)
    }
)

$files = Get-ChildItem -LiteralPath $OutputRoot -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($OutputRoot.Length).TrimStart('\').Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'Actorwright'
    version = '1.0.0-preview.281'
    sourceLine = 'preview.281-public'
    runtime = $Runtime
    privateOnly = $true
    runtimeAuthority = $false
    commandCount = 142
    embeddedResourceClosures = $embeddedResourceClosures
    files = @($files)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content `
    -LiteralPath (Join-Path $OutputRoot 'manifest.json') -Encoding UTF8

$verificationOutput = & $python `
    (Join-Path $projectRoot 'tools\release\verify_release.py') `
    $OutputRoot --package-staging --json
if ($LASTEXITCODE -ne 0) {
    throw "Package verifier failed with exit code $LASTEXITCODE"
}
$verification = $verificationOutput | ConvertFrom-Json
$expectedProtocolV2WorkflowCommands = @(
    'npc assembly preflight',
    'npc create-from-jslot',
    'npc finish analyze',
    'npc finish apply',
    'npc finish verify',
    'preset inspect',
    'preview npc',
    'workspace preflight'
)
$actualProtocolV2WorkflowCommands = @($verification.protocolV2WorkflowCommands)
if ($verification.status -ne 'PASS' -or
    $verification.protocolV2Kernel -ne $true -or
    ($actualProtocolV2WorkflowCommands -join "`n") -ne
        ($expectedProtocolV2WorkflowCommands -join "`n") -or
    @($verification.protocolV2ConsumerRequiredCommands).Count -ne 1 -or
    $verification.protocolV2ConsumerRequiredCommands[0] -ne 'preview npc') {
    throw 'Package verifier did not admit the protocol 2 kernel.'
}
$verificationJson = [string]::Join(
    [Environment]::NewLine,
    @($verificationOutput))
$null = & (Join-Path $projectRoot `
    'tools\build\write_package_verification_evidence.ps1') `
    -PackageRoot $OutputRoot `
    -VerificationJson $verificationJson

function Assert-PackageFirewallReport([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or
        -not ($item -is [IO.FileInfo]) -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Package compatibility firewall report is missing, empty, or not an ordinary file: $Path"
    }
}

if ($Configuration -eq 'Release') {
    $sourceCli = Join-Path $projectRoot `
        'src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe'
    $firewallScript = Join-Path $projectRoot `
        'tools\verification\compatibility-firewall.ps1'
    $firewallReportRoot = Join-Path $projectRoot 'artifacts\test-work'
    New-Item -ItemType Directory -Force -Path $firewallReportRoot | Out-Null
    $firewallReport = Join-Path $firewallReportRoot (
        "compatibility-firewall-package-" + [Guid]::NewGuid().ToString('N') + '.json')
    & $firewallScript verify `
        -Tier Package `
        -Baseline publicSynthetic `
        -SourceCli $sourceCli `
        -ReleaseRoot $OutputRoot `
        -Output $firewallReport
    if ($LASTEXITCODE -ne 0) {
        throw "Package compatibility firewall failed (exit $LASTEXITCODE)"
    }
    Assert-PackageFirewallReport -Path $firewallReport
}

Write-Output "Actorwright package: PASS $OutputRoot"
