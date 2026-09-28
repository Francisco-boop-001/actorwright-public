param(
    [string]$Version = '1.0.0-preview.1',
    [string]$EvidenceStamp = '',
    [switch]$ValidateAuthoritiesOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$toolWorkspaceRoot = $workspaceRoot
$dotnetRelative = 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$localDotnet = Join-Path $toolWorkspaceRoot $dotnetRelative
if (!(Test-Path -LiteralPath $localDotnet -PathType Leaf)) {
    $gitCommonDirectory = (& git -C $workspaceRoot rev-parse --git-common-dir)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommonDirectory)) {
        throw 'could not resolve the Git common workspace for pinned package tools'
    }
    if (![System.IO.Path]::IsPathRooted($gitCommonDirectory)) {
        $gitCommonDirectory = Join-Path $workspaceRoot $gitCommonDirectory
    }
    $toolWorkspaceRoot = (Resolve-Path (Join-Path $gitCommonDirectory '..')).Path
    foreach ($required in @(
        (Join-Path $toolWorkspaceRoot 'AGENTS.md'),
        (Join-Path $toolWorkspaceRoot 'WORKSPACE_MANIFEST.json'),
        (Join-Path $toolWorkspaceRoot $dotnetRelative)
    )) {
        if (!(Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Git common workspace lacks pinned package authority: $required"
        }
    }
}
$dotnet = (Resolve-Path (Join-Path $toolWorkspaceRoot $dotnetRelative)).Path
$fixtureRelatives = @(
    '01-source-copies\m4-fixtures\fo4-looksmenu.json',
    '01-source-copies\m4-fixtures\sse-racemenu.jslot',
    '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp',
    '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp',
    '01-source-copies\m6-preview-animation-assets\sse\femalehead.nif',
    '01-source-copies\m6-preview-animation-assets\sse\hww0_mt_idle.hkx',
    '01-source-copies\m6-preview-animation-assets\sse\skeleton_female_sse.hkx',
    '01-source-copies\m6-preview-assets\sse\hair.nif'
)
$fixtureProjectRoot = $projectRoot
$missingLocalFixtures = @(
    $fixtureRelatives | Where-Object {
        !(Test-Path -LiteralPath (Join-Path $fixtureProjectRoot $_) -PathType Leaf)
    }
)
if ($missingLocalFixtures.Count -gt 0) {
    $fixtureProjectRoot = Join-Path $toolWorkspaceRoot 'projects\NpcManagerReimplementation'
    if (!(Test-Path -LiteralPath $fixtureProjectRoot -PathType Container)) {
        throw "Git common workspace lacks the authentic package fixture authority: $fixtureProjectRoot"
    }
    $fixtureProjectRoot = (Resolve-Path $fixtureProjectRoot).Path
}
foreach ($fixtureRelative in $fixtureRelatives) {
    $fixturePath = Join-Path $fixtureProjectRoot $fixtureRelative
    if (!(Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
        throw "authentic package fixture is missing: $fixturePath"
    }
    $fixtureItem = Get-Item -LiteralPath $fixturePath -Force
    if (($fixtureItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "authentic package fixture must not be a reparse point: $fixturePath"
    }
}
$packageRoot = Join-Path $projectRoot "04-packages\npcmanager-$Version"
$zipPath = Join-Path $projectRoot "04-packages\npcmanager-$Version.zip"
$publishRoot = Join-Path $projectRoot "03-builds\work\m8-package-publish-$Version"
$stamp = if ([string]::IsNullOrWhiteSpace($EvidenceStamp)) { (Get-Date).ToString('yyyy-MM-dd') } else { $EvidenceStamp }
$reportPath = Join-Path $projectRoot "05-reports\m8-package-acceptance-$stamp.json"
$reportMdPath = Join-Path $projectRoot "05-reports\m8-package-acceptance-$stamp.md"

if (Test-Path -LiteralPath $packageRoot) { throw "refusing to overwrite existing package root: $packageRoot" }
if (Test-Path -LiteralPath $zipPath) { throw "refusing to overwrite existing package archive: $zipPath" }
if (Test-Path -LiteralPath $reportPath) { throw "refusing to overwrite existing package report: $reportPath" }
if (Test-Path -LiteralPath $reportMdPath) { throw "refusing to overwrite existing package report: $reportMdPath" }
if (Test-Path -LiteralPath $publishRoot) { throw "refusing to overwrite existing package publish root: $publishRoot" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$(Split-Path -Parent $dotnet);$env:PATH"
$env:NUGET_PACKAGES = Join-Path $toolWorkspaceRoot 'tools\external\nuget-packages-m2'

$runtimePacks = @(
    @{
        Path = Join-Path $toolWorkspaceRoot 'tools\external\nuget-feed\microsoft.netcore.app.runtime.win-x64.10.0.9.nupkg'
        Sha256 = 'B681046AEF7CC0FFA9FEC3AD865F033C7165300BD295C4A8902F24150A3FA414'
    },
    @{
        Path = Join-Path $toolWorkspaceRoot 'tools\external\nuget-feed\microsoft.windowsdesktop.app.runtime.win-x64.10.0.9.nupkg'
        Sha256 = '582C8AE14CD3C0F651D8BDD6FCD0D7716489B0498D96DECB6FA3220330593155'
    },
    @{
        Path = Join-Path $toolWorkspaceRoot 'tools\external\nuget-feed\microsoft.aspnetcore.app.runtime.win-x64.10.0.9.nupkg'
        Sha256 = '94D77B81A14FC60A189067127706C9A93D14FAF3A92DA07F0A29132B32507B05'
    }
)
foreach ($runtimePack in $runtimePacks) {
    if (!(Test-Path -LiteralPath $runtimePack.Path -PathType Leaf)) {
        throw "pinned runtime pack is missing: $($runtimePack.Path)"
    }
    $actualHash = (Get-FileHash -LiteralPath $runtimePack.Path -Algorithm SHA256).Hash
    if ($actualHash -ne $runtimePack.Sha256) {
        throw "pinned runtime pack hash mismatch: $($runtimePack.Path)"
    }
}

if ($ValidateAuthoritiesOnly) {
    [pscustomobject]@{
        schemaVersion = 1
        artifactKind = 'npc-manager-package-authority-validation'
        sourceWorkspace = $workspaceRoot
        toolWorkspace = $toolWorkspaceRoot
        fixtureProject = $fixtureProjectRoot
        dotnet = [pscustomobject]@{
            path = $dotnet
            sha256 = (Get-FileHash -LiteralPath $dotnet -Algorithm SHA256).Hash
        }
        runtimePacks = @(
            $runtimePacks | ForEach-Object {
                [pscustomobject]@{
                    path = $_.Path
                    sha256 = $_.Sha256
                }
            }
        )
        fixtures = @(
            $fixtureRelatives | ForEach-Object {
                $path = Join-Path $fixtureProjectRoot $_
                [pscustomobject]@{
                    relativePath = $_
                    path = $path
                    length = (Get-Item -LiteralPath $path).Length
                    sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
                }
            }
        )
        outputsAbsent = @(
            $packageRoot,
            $zipPath,
            $publishRoot,
            $reportPath,
            $reportMdPath
        ) | ForEach-Object { !(Test-Path -LiteralPath $_) }
        outcome = 'PASS'
    } | ConvertTo-Json -Depth 6
    return
}

New-Item -ItemType Directory -Path $packageRoot, $publishRoot | Out-Null
$cliPublish = Join-Path $publishRoot 'cli'
$desktopPublish = Join-Path $publishRoot 'desktop'
$cliProject = Join-Path $projectRoot 'src\NpcManager.Cli\NpcManager.Cli.csproj'
$desktopProject = Join-Path $projectRoot 'src\NpcManager.Desktop\NpcManager.Desktop.csproj'
& $dotnet restore $cliProject --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw "CLI locked restore failed with exit code $LASTEXITCODE" }
& $dotnet restore $desktopProject --runtime win-x64 --locked-mode
if ($LASTEXITCODE -ne 0) { throw "desktop locked restore failed with exit code $LASTEXITCODE" }
& $dotnet publish $cliProject --configuration Release --runtime win-x64 --no-restore --self-contained true --output $cliPublish -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "CLI publish failed with exit code $LASTEXITCODE" }
& $dotnet publish $desktopProject --configuration Release --runtime win-x64 --no-restore --self-contained true --output $desktopPublish -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed with exit code $LASTEXITCODE" }

function Copy-IntoPackage([string]$source, [string]$relativeDestination) {
    $destination = Join-Path $packageRoot $relativeDestination
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}

function Copy-NuGetEntryIntoPackage(
    [string]$archivePath,
    [string]$entryName,
    [string]$relativeDestination
) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $destination = Join-Path $packageRoot $relativeDestination
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        throw "refusing to overwrite package license artifact: $destination"
    }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entry = $archive.GetEntry($entryName)
        if ($null -eq $entry) { throw "NuGet package entry is missing: ${archivePath}::${entryName}" }
        $input = $entry.Open()
        $output = [System.IO.File]::Open($destination, [System.IO.FileMode]::CreateNew)
        try { $input.CopyTo($output) }
        finally { $output.Dispose(); $input.Dispose() }
    }
    finally { $archive.Dispose() }
}

Copy-Item -LiteralPath $cliPublish -Destination (Join-Path $packageRoot 'cli') -Recurse
Copy-Item -LiteralPath $desktopPublish -Destination (Join-Path $packageRoot 'desktop') -Recurse
Copy-IntoPackage (Join-Path $projectRoot 'README.md') 'README.md'
Copy-IntoPackage (Join-Path $projectRoot 'LICENSE') 'licenses\LICENSE'
Copy-IntoPackage (Join-Path $projectRoot 'NOTICE') 'licenses\NOTICE'
Copy-IntoPackage (Join-Path $projectRoot 'docs\licenses.md') 'licenses\licenses.md'
Copy-IntoPackage (Join-Path $projectRoot 'docs\third-party-licenses.md') 'licenses\third-party-licenses.md'
Copy-IntoPackage (Join-Path $projectRoot '05-reports\m2-mutagen-license-review.json') 'licenses\m2-mutagen-license-review.json'
Copy-IntoPackage (Join-Path $projectRoot '05-reports\m8-license-closure-2026-07-17.json') 'licenses\m8-license-closure.json'
Copy-IntoPackage (Join-Path $projectRoot '05-reports\m8-license-closure-2026-07-17.md') 'licenses\m8-license-closure.md'
Copy-IntoPackage (Join-Path $projectRoot '05-reports\m8-sbom-license-acceptance-2026-07-17.json') 'licenses\m8-sbom-license-acceptance.json'
Copy-IntoPackage (Join-Path $projectRoot '05-reports\sbom-cyclonedx-2026-07-17.json') 'licenses\sbom-cyclonedx.json'
$reloadedLicense = Join-Path $toolWorkspaceRoot 'tools\external\nuget-packages-m2\reloaded.memory\9.4.1\LICENSE.md'
if (!(Test-Path -LiteralPath $reloadedLicense)) { throw "pinned Reloaded.Memory license text is missing: $reloadedLicense" }
Copy-IntoPackage $reloadedLicense 'licenses\Reloaded.Memory-9.4.1-LICENSE.md'
Copy-NuGetEntryIntoPackage $runtimePacks[0].Path 'LICENSE.TXT' 'licenses\Microsoft.NETCore.App.Runtime-10.0.9-LICENSE.txt'
Copy-NuGetEntryIntoPackage $runtimePacks[0].Path 'THIRD-PARTY-NOTICES.TXT' 'licenses\Microsoft.NETCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt'
Copy-NuGetEntryIntoPackage $runtimePacks[1].Path 'LICENSE' 'licenses\Microsoft.WindowsDesktop.App.Runtime-10.0.9-LICENSE.txt'
Copy-NuGetEntryIntoPackage $runtimePacks[2].Path 'LICENSE.txt' 'licenses\Microsoft.AspNetCore.App.Runtime-10.0.9-LICENSE.txt'
Copy-NuGetEntryIntoPackage $runtimePacks[2].Path 'THIRD-PARTY-NOTICES.TXT' 'licenses\Microsoft.AspNetCore.App.Runtime-10.0.9-THIRD-PARTY-NOTICES.txt'

foreach ($relative in @('docs', 'tasks')) {
    $sourceRoot = Join-Path $projectRoot $relative
    Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | ForEach-Object {
        $path = $_.FullName.Substring($projectRoot.Length).TrimStart('\\')
        Copy-IntoPackage $_.FullName (Join-Path 'source' $path)
    }
}
foreach ($relative in @('src', 'tests', 'tools')) {
    $sourceRoot = Join-Path $projectRoot $relative
    Get-ChildItem -LiteralPath $sourceRoot -File -Recurse | Where-Object {
        $_.FullName -notmatch '\\(bin|obj|__pycache__)\\'
    } | ForEach-Object {
        $path = $_.FullName.Substring($projectRoot.Length).TrimStart('\\')
        Copy-IntoPackage $_.FullName (Join-Path 'source' $path)
    }
}
# These adapters live at the workspace level because the CLI binds them to the
# K-only toolchain. Include them in the source snapshot so the package carries
# every renderer referenced by PROJECT_MANIFEST.json.
foreach ($workspaceRelative in @(
    'tools\rendering\hair_zap.py',
    'tools\rendering\render_preview_scene.py',
    'tools\rendering\render_npc_preview_bundle.py',
    'tools\rendering\export_preview_nif.py',
    'tools\rendering\export_facegeom_nif.py',
    'tools\rendering\nif_geometry_readback.py'
)) {
    Copy-IntoPackage (Join-Path $workspaceRoot $workspaceRelative) (Join-Path 'source' $workspaceRelative)
}
foreach ($relative in @(
    'global.json', 'Directory.Build.props', 'Directory.Build.targets', 'nuget.config',
    '.editorconfig', 'PROJECT_MANIFEST.json', '05-reports\feature-ledger.json',
    '05-reports\release-gate.json', '05-reports\source-copy-sha256-manifest.csv',
    '05-reports\npc-manager-preview224-dual-tone-hair-verification-20260731.json',
    '05-reports\npc-manager-preview224-dual-tone-hair-verification-20260731.md',
    '05-reports\npc-manager-test-evidence-preview224-20260731-final2.json',
    '05-reports\npc-manager-test-evidence-preview224-20260731-final2.junit.xml',
    '05-reports\m8-p12-completeness-acceptance-2026-07-17.json',
    '05-reports\m8-license-closure-2026-07-17.json',
    '05-reports\m8-security-scan-2026-07-17.json',
    '05-reports\m8-dependency-vulnerability-2026-07-17.json',
    '05-reports\m8-dependency-vulnerability-acceptance-2026-07-17.md',
    '05-reports\m8-dependency-vulnerability-public-2026-07-17.json',
    '05-reports\m8-dependency-vulnerability-public-acceptance-2026-07-17.md',
    '05-reports\m8-performance-benchmark-2026-07-17.json',
    '05-reports\m8-docs-validation-2026-07-17.json',
    '05-reports\m8-docs-acceptance-2026-07-17.md',
    '05-reports\m8-runtime-smoke-readiness-2026-07-17.json',
    '05-reports\m8-runtime-smoke-readiness-2026-07-17.md',
    '05-reports\m9-open-gaps-audit-2026-07-17.json',
    '05-reports\m9-open-gaps-acceptance-2026-07-17.md',
    '05-reports\m9-open-gaps-audit-2026-07-18.json',
    '05-reports\m9-open-gaps-audit-2026-07-18.md',
    '05-reports\m9-open-gaps-audit-2026-07-18b.json',
    '05-reports\m9-open-gaps-audit-2026-07-18b.md',
    '05-reports\m9-open-gaps-audit-2026-07-18c.json',
    '05-reports\m9-open-gaps-audit-2026-07-18c.md',
    '05-reports\m9-open-gaps-audit-2026-07-18d.json',
    '05-reports\m9-open-gaps-audit-2026-07-18d.md',
    '05-reports\m9-feature-evidence-audit-2026-07-18.json',
    '05-reports\m9-feature-claim-audit-2026-07-18e.json',
    '05-reports\m9-performance-benchmark-2026-07-18e.json',
    '05-reports\m9-performance-benchmark-2026-07-18e.md',
    '05-reports\m9-performance-acceptance-2026-07-18e.json',
    '05-reports\m9-performance-benchmark-2026-07-18f.json',
    '05-reports\m9-performance-benchmark-2026-07-18f.md',
    '05-reports\m9-performance-acceptance-2026-07-18f.json',
    '05-reports\m9-performance-benchmark-2026-07-18i.json',
    '05-reports\m9-performance-benchmark-2026-07-18i.md',
    '05-reports\m9-performance-acceptance-2026-07-18i.json',
    '05-reports\m9-performance-benchmark-2026-07-18j.json',
    '05-reports\m9-performance-acceptance-2026-07-18j.json',
    '05-reports\m9-facegen-provider-paths-acceptance-2026-07-18.json',
    '05-reports\m9-facegen-provider-paths-acceptance-2026-07-18.md',
    '05-reports\m9-facegen-pack-acceptance-2026-07-18.json',
    '05-reports\m9-facegen-pack-acceptance-2026-07-18.md',
    '05-reports\m7-runtime-script-vmad-inspection-acceptance-2026-07-18.json',
    '05-reports\m7-runtime-script-vmad-inspection-acceptance-2026-07-18.md',
    '05-reports\m9-docs-validation-2026-07-18h.json',
    '05-reports\m9-security-scan-2026-07-18h.json',
    '05-reports\m9-docs-validation-2026-07-18k.json',
    '05-reports\m9-security-scan-2026-07-18k.json'
    ,'05-reports\m9-docs-validation-2026-07-18o.json'
    ,'05-reports\m9-security-scan-2026-07-18o.json'
    ,'05-reports\m9-docs-validation-2026-07-18t.json'
    ,'05-reports\m9-security-scan-2026-07-18t.json'
    ,'05-reports\m9-docs-validation-2026-07-18u.json'
    ,'05-reports\m9-security-scan-2026-07-18u.json'
    ,'05-reports\m9-feature-evidence-audit-2026-07-18e.json'
    ,'05-reports\m9-docs-validation-2026-07-18v.json'
     ,'05-reports\m9-security-scan-2026-07-18v.json'
     ,'05-reports\m9-docs-validation-2026-07-18w.json'
     ,'05-reports\m9-security-scan-2026-07-18w.json'
     ,'05-reports\m9-docs-validation-2026-07-18x.json'
     ,'05-reports\m9-security-scan-2026-07-18x.json'
     ,'05-reports\m9-docs-validation-2026-07-18y.json'
     ,'05-reports\m9-security-scan-2026-07-18y.json'
      ,'05-reports\m9-docs-validation-2026-07-18af.json'
      ,'05-reports\m9-security-scan-2026-07-18af.json'
      ,'05-reports\m9-docs-validation-2026-07-18ag.json'
      ,'05-reports\m9-security-scan-2026-07-18ag.json'
     ,'05-reports\m9-feature-evidence-audit-2026-07-18f.json'
     ,'05-reports\m9-feature-evidence-audit-2026-07-18g.json'
     ,'05-reports\m9-feature-evidence-audit-2026-07-18h.json'
     ,'05-reports\m9-feature-evidence-audit-2026-07-18i.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18j.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18k.json'
    ,'05-reports\m9-feature-evidence-audit-2026-07-18d.json'
     ,'05-reports\m9-package-command-acceptance-2026-07-18.json'
     ,'05-reports\m9-package-command-acceptance-2026-07-18.md'
     ,'05-reports\m9-preview-morph-render-acceptance-2026-07-18.json'
      ,'05-reports\m9-preview-morph-render-acceptance-2026-07-18.md'
      ,'05-reports\m6-preview-nif-binary-morph-acceptance-2026-07-18.json'
      ,'05-reports\m6-preview-nif-binary-morph-acceptance-2026-07-18.md'
      ,'05-reports\m6-preview-skinned-semantics-acceptance-2026-07-18.json'
      ,'05-reports\m6-preview-skinned-semantics-acceptance-2026-07-18.md'
      ,'05-reports\m6-preview-animation-render-acceptance-2026-07-18.json'
      ,'05-reports\m6-preview-animation-render-acceptance-2026-07-18.md'
      ,'05-reports\m9-docs-validation-2026-07-18z.json'
      ,'05-reports\m9-security-scan-2026-07-18z.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18l.json'
      ,'05-reports\m9-docs-validation-2026-07-18aa.json'
      ,'05-reports\m9-security-scan-2026-07-18aa.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18m.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18n.json'
      ,'05-reports\m9-docs-validation-2026-07-18ab.json'
      ,'05-reports\m9-security-scan-2026-07-18ac.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18o.json'
      ,'05-reports\m9-docs-validation-2026-07-18ad.json'
      ,'05-reports\m9-security-scan-2026-07-18ad.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18p.json'
      ,'05-reports\m9-docs-validation-2026-07-18ae.json'
      ,'05-reports\m9-security-scan-2026-07-18ae.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18q.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ao.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ao.md'
      ,'05-reports\m8-package-acceptance-2026-07-18aw.json'
      ,'05-reports\m8-package-acceptance-2026-07-18aw.md'
      ,'05-reports\m8-package-acceptance-2026-07-18ax.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ax.md'
      ,'05-reports\m8-package-acceptance-2026-07-18ay.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ay.md'
      ,'05-reports\m8-package-acceptance-2026-07-18az.json'
      ,'05-reports\m8-package-acceptance-2026-07-18az.md'
      ,'05-reports\m8-package-acceptance-2026-07-18ba.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ba.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bc.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bc.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bd.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bd.md'
      ,'05-reports\m8-package-acceptance-2026-07-18be.json'
      ,'05-reports\m8-package-acceptance-2026-07-18be.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bf.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bf.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bg.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bg.md'
      ,'05-reports\m8-package-acceptance-2026-07-18hn.json'
      ,'05-reports\m8-package-acceptance-2026-07-18hn.md'
      ,'05-reports\m8-package-acceptance-2026-07-18ho.json'
      ,'05-reports\m8-package-acceptance-2026-07-18ho.md'
      ,'05-reports\m8-package-acceptance-2026-07-18hp.json'
      ,'05-reports\m8-package-acceptance-2026-07-18hp.md'
      ,'05-reports\m8-package-acceptance-2026-07-19g.json'
      ,'05-reports\m8-package-acceptance-2026-07-19g.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bi.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bi.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bj.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bj.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bk.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bk.md'
      ,'05-reports\m8-package-acceptance-2026-07-18bm.json'
      ,'05-reports\m8-package-acceptance-2026-07-18bm.md'
      ,'05-reports\m8-package-acceptance-2026-07-18gv.json'
      ,'05-reports\m8-package-acceptance-2026-07-18gv.md'
      ,'05-reports\m6-preview-hair-zap-acceptance-2026-07-18.json'
      ,'05-reports\m6-preview-hair-zap-acceptance-2026-07-18.md'
      ,'05-reports\m9-docs-validation-2026-07-18ai.json'
      ,'05-reports\m9-security-scan-2026-07-18ai.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18r.json'
      ,'05-reports\m9-docs-validation-2026-07-18ak.json'
      ,'05-reports\m9-security-scan-2026-07-18ak.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18t.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18u.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18v.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18w.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18e.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18e.md'
      ,'05-reports\m9-docs-validation-2026-07-18al.json'
      ,'05-reports\m9-security-scan-2026-07-18al.json'
      ,'05-reports\m9-docs-validation-2026-07-18am.json'
      ,'05-reports\m9-security-scan-2026-07-18am.json'
      ,'05-reports\m9-docs-validation-2026-07-18ao.json'
      ,'05-reports\m9-security-scan-2026-07-18ao.json'
      ,'05-reports\m9-ui-shell-acceptance-2026-07-18.json'
      ,'05-reports\m9-ui-shell-acceptance-2026-07-18.md'
      ,'05-reports\m10-plugin-surface-audit-acceptance-2026-07-18.json'
      ,'05-reports\m10-plugin-surface-audit-acceptance-2026-07-18.md'
      ,'05-reports\m9-plugin-deploy-acceptance-2026-07-18.json'
      ,'05-reports\m9-plugin-deploy-acceptance-2026-07-18.md'
      ,'05-reports\m9-facegen-deploy-acceptance-2026-07-18.json'
      ,'05-reports\m9-facegen-deploy-acceptance-2026-07-18.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18ba.json'
      ,'05-reports\m9-docs-validation-2026-07-18ba.json'
      ,'05-reports\m9-security-scan-2026-07-18ba.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bb.json'
      ,'05-reports\m9-docs-validation-2026-07-18bb.json'
      ,'05-reports\m9-security-scan-2026-07-18bb.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bc.json'
      ,'05-reports\m9-docs-validation-2026-07-18bc.json'
      ,'05-reports\m9-security-scan-2026-07-18bc.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bd.json'
      ,'05-reports\m9-docs-validation-2026-07-18bd.json'
      ,'05-reports\m9-security-scan-2026-07-18bd.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18be.json'
      ,'05-reports\m9-docs-validation-2026-07-18be.json'
      ,'05-reports\m9-security-scan-2026-07-18be.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bf.json'
      ,'05-reports\m9-docs-validation-2026-07-18bf.json'
      ,'05-reports\m9-security-scan-2026-07-18bf.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bg.json'
      ,'05-reports\m9-security-scan-2026-07-18bg.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bh.json'
      ,'05-reports\m9-security-scan-2026-07-18bh.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18bi.json'
      ,'05-reports\m9-docs-validation-2026-07-18bg.json'
      ,'05-reports\m9-security-scan-2026-07-18bi.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19d.json'
      ,'05-reports\m9-docs-validation-2026-07-19d.json'
      ,'05-reports\m9-security-scan-2026-07-19d.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19f.json'
      ,'05-reports\m9-docs-validation-2026-07-19f.json'
      ,'05-reports\m9-security-scan-2026-07-19f.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19i.json'
      ,'05-reports\m9-docs-validation-2026-07-19i.json'
      ,'05-reports\m9-security-scan-2026-07-19i.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19j.json'
      ,'05-reports\m9-docs-validation-2026-07-19j.json'
      ,'05-reports\m9-security-scan-2026-07-19j.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19k.json'
      ,'05-reports\m9-docs-validation-2026-07-19k.json'
      ,'05-reports\m9-security-scan-2026-07-19k.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19s.json'
      ,'05-reports\m9-docs-validation-2026-07-19s.json'
      ,'05-reports\m9-security-scan-2026-07-19s.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19u.json'
      ,'05-reports\m9-docs-validation-2026-07-19u.json'
      ,'05-reports\m9-security-scan-2026-07-19u.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19w.json'
      ,'05-reports\m9-docs-validation-2026-07-19w.json'
      ,'05-reports\m9-security-scan-2026-07-19w.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19x.json'
      ,'05-reports\m9-docs-validation-2026-07-19x.json'
      ,'05-reports\m9-security-scan-2026-07-19x.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19ad.json'
      ,'05-reports\m9-docs-validation-2026-07-19ad.json'
      ,'05-reports\m9-security-scan-2026-07-19ad.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19ae.json'
      ,'05-reports\m9-docs-validation-2026-07-19ae.json'
      ,'05-reports\m9-security-scan-2026-07-19ae.json'
      ,'05-reports\m8-package-acceptance-2026-07-19j.json'
      ,'05-reports\m8-package-acceptance-2026-07-19j.md'
      ,'05-reports\m8-package-acceptance-2026-07-19k.json'
      ,'05-reports\m8-package-acceptance-2026-07-19k.md'
      ,'05-reports\m8-package-acceptance-2026-07-19l.json'
      ,'05-reports\m8-package-acceptance-2026-07-19l.md'
      ,'05-reports\m8-package-acceptance-2026-07-19m.json'
      ,'05-reports\m8-package-acceptance-2026-07-19m.md'
      ,'05-reports\m9-runtime-smoke-validator-acceptance-2026-07-18ap.json'
      ,'05-reports\m9-runtime-smoke-validator-acceptance-2026-07-18ap.md'
      ,'05-reports\m10-plugin-surface-audit-acceptance-2026-07-18.json'
      ,'05-reports\m10-plugin-surface-audit-acceptance-2026-07-18.md'
      ,'05-reports\m9-open-gaps-audit-2026-07-18g.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18g.md'
      ,'05-reports\m9-open-gaps-audit-2026-07-18h.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18h.md'
      ,'05-reports\m9-open-gaps-audit-2026-07-18i.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18i.md'
       ,'05-reports\m9-open-gaps-audit-2026-07-19a.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19a.md'
       ,'05-reports\m9-open-gaps-audit-2026-07-19ag.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19ag.md'
       ,'05-reports\m9-facetint-provider-binding-2026-07-19ag.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19ag.json'
       ,'05-reports\m9-docs-validation-2026-07-19ag.json'
       ,'05-reports\m9-security-scan-2026-07-19ag.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19ai.json'
       ,'05-reports\m9-docs-validation-2026-07-19ai.json'
       ,'05-reports\m9-security-scan-2026-07-19ai.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19ak.json'
       ,'05-reports\m9-docs-validation-2026-07-19ak.json'
,'05-reports\m9-security-scan-2026-07-19ak.json'
 ,'05-reports\m6-preview-face-cull-acceptance-2026-07-19.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19al.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19al.json'
 ,'05-reports\m9-docs-validation-2026-07-19al.json'
 ,'05-reports\m9-security-scan-2026-07-19al.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19ap.json'
 ,'05-reports\m9-docs-validation-2026-07-19ap.json'
 ,'05-reports\m9-security-scan-2026-07-19ap.json'
 ,'05-reports\m6-preview-animation-list-acceptance-2026-07-19.json'
 ,'05-reports\m6-preview-animation-tree-acceptance-2026-07-19.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19at.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19at.json'
 ,'05-reports\m9-docs-validation-2026-07-19at.json'
 ,'05-reports\m9-security-scan-2026-07-19at.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19au.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19au.json'
 ,'05-reports\m9-docs-validation-2026-07-19au.json'
 ,'05-reports\m9-security-scan-2026-07-19au.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19av.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19av.json'
 ,'05-reports\m9-docs-validation-2026-07-19av.json'
 ,'05-reports\m9-security-scan-2026-07-19av.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19az.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19az.json'
 ,'05-reports\m9-docs-validation-2026-07-19az.json'
 ,'05-reports\m9-security-scan-2026-07-19az.json'
 ,'05-reports\m9-ui-animation-picker-acceptance-2026-07-19.json'
 ,'05-reports\m9-cli-router-refactor-acceptance-2026-07-19.json'
 ,'05-reports\m9-npc-mutation-path-policy-acceptance-2026-07-19.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19be.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19be.json'
 ,'05-reports\m9-docs-validation-2026-07-19be.json'
 ,'05-reports\m9-security-scan-2026-07-19be.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19bg.json'
 ,'05-reports\m9-docs-validation-2026-07-19bg.json'
 ,'05-reports\m9-security-scan-2026-07-19bg.json'
 ,'05-reports\m9-racemenu-codec-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-mutation-handler-responsibility-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-skyrim-body-transform-codec-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-npc-mutation-service-responsibility-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-body-sidecar-inspection-responsibility-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-cli-router-responsibility-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-cli-catalog-router-audit-2026-07-19.json'
 ,'05-reports\m9-face-tint-build-responsibility-split-acceptance-2026-07-19.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19bk.json'
 ,'05-reports\m9-open-gaps-audit-2026-07-19bk.md'
 ,'05-reports\m9-feature-evidence-audit-2026-07-19bo.json'
 ,'05-reports\m9-docs-validation-2026-07-19bo.json'
 ,'05-reports\m9-security-scan-2026-07-19bo.json'
 ,'05-reports\m9-docs-validation-2026-07-19bi.json'
 ,'05-reports\m9-security-scan-2026-07-19bi.json'
 ,'05-reports\m9-feature-evidence-audit-2026-07-18ag.json'
      ,'05-reports\m9-docs-validation-2026-07-18az.json'
      ,'05-reports\m9-security-scan-2026-07-18az.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18f.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18f.md'
      ,'05-reports\m9-open-gaps-audit-2026-07-18g.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-18g.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18x.json'
      ,'05-reports\m9-docs-validation-2026-07-18ap.json'
      ,'05-reports\m9-security-scan-2026-07-18ap.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18y.json'
      ,'05-reports\m9-docs-validation-2026-07-18aq.json'
      ,'05-reports\m9-security-scan-2026-07-18aq.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18z.json'
      ,'05-reports\m9-docs-validation-2026-07-18as.json'
      ,'05-reports\m9-security-scan-2026-07-18as.json'
      ,'05-reports\m9-runtime-smoke-code-review-2026-07-18.json'
      ,'05-reports\m9-runtime-smoke-code-review-2026-07-18.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18aa.json'
      ,'05-reports\m9-docs-validation-2026-07-18at.json'
      ,'05-reports\m9-security-scan-2026-07-18at.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18ab.json'
      ,'05-reports\m9-docs-validation-2026-07-18au.json'
      ,'05-reports\m9-security-scan-2026-07-18au.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18ac.json'
      ,'05-reports\m9-docs-validation-2026-07-18av.json'
      ,'05-reports\m9-security-scan-2026-07-18av.json'
      ,'05-reports\m9-feature-evidence-audit-2026-07-18ad.json'
      ,'05-reports\m9-docs-validation-2026-07-18aw.json'
      ,'05-reports\m9-security-scan-2026-07-18aw.json'
      ,'05-reports\m9-bethesda-npc-adapter-responsibility-split-acceptance-2026-07-19.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bl.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bl.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19bp.json'
      ,'05-reports\m9-docs-validation-2026-07-19bp.json'
      ,'05-reports\m9-security-scan-2026-07-19bp.json'
      ,'05-reports\m9-looksmenu-codec-responsibility-split-acceptance-2026-07-19.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bm.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bm.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19bq.json'
      ,'05-reports\m9-docs-validation-2026-07-19bq.json'
      ,'05-reports\m9-security-scan-2026-07-19bq.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bn.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bn.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19br.json'
      ,'05-reports\m9-docs-validation-2026-07-19br.json'
      ,'05-reports\m9-security-scan-2026-07-19br.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bo.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bo.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19bs.json'
      ,'05-reports\m9-docs-validation-2026-07-19bs.json'
      ,'05-reports\m9-security-scan-2026-07-19bs.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bp.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bp.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19bt.json'
      ,'05-reports\m9-docs-validation-2026-07-19bt.json'
      ,'05-reports\m9-security-scan-2026-07-19bt.json'
      ,'05-reports\m9-racemenu-reader-responsibility-split-acceptance-2026-07-19.json'
      ,'05-reports\m9-preview-nif-exporter-responsibility-split-acceptance-2026-07-19.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bq.json'
      ,'05-reports\m9-open-gaps-audit-2026-07-19bq.md'
      ,'05-reports\m9-feature-evidence-audit-2026-07-19bu.json'
      ,'05-reports\m9-docs-validation-2026-07-19bu.json'
       ,'05-reports\m9-security-scan-2026-07-19bu.json'
       ,'05-reports\m9-preview-scene-service-responsibility-split-acceptance-2026-07-19.json'
       ,'05-reports\m9-bethesda-plugin-verifier-responsibility-split-acceptance-2026-07-19.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19br.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19bv.json'
       ,'05-reports\m9-docs-validation-2026-07-19bv.json'
       ,'05-reports\m9-security-scan-2026-07-19bv.json'
       ,'05-reports\m9-racemenu-sculpt-patch-responsibility-split-acceptance-2026-07-19.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bs.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19bw.json'
       ,'05-reports\m9-docs-validation-2026-07-19bw.json'
       ,'05-reports\m9-security-scan-2026-07-19bw.json'
       ,'05-reports\m9-object-template-binary-writer-responsibility-split-acceptance-2026-07-19.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bt.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19bx.json'
       ,'05-reports\m9-docs-validation-2026-07-19bx.json'
       ,'05-reports\m9-security-scan-2026-07-19bx.json'
       ,'05-reports\m9-feature-claim-audit-2026-07-19av.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bu.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bv.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19by.json'
       ,'05-reports\m9-docs-validation-2026-07-19by.json'
       ,'05-reports\m9-security-scan-2026-07-19by.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bw.json'
       ,'05-reports\m9-feature-evidence-audit-2026-07-19bz.json'
       ,'05-reports\m9-docs-validation-2026-07-19bz.json'
       ,'05-reports\m9-security-scan-2026-07-19bz.json'
       ,'05-reports\m9-feature-claim-audit-2026-07-19ca.json'
       ,'05-reports\m9-runtime-blocker-2026-07-19.json'
       ,'05-reports\m9-open-gaps-audit-2026-07-19bx.json'
   )) {
    Copy-IntoPackage (Join-Path $projectRoot $relative) (Join-Path 'evidence' (Split-Path $relative -Leaf))
}

foreach ($fixture in @(
    '01-source-copies\m4-fixtures\fo4-looksmenu.json',
    '01-source-copies\m4-fixtures\sse-racemenu.jslot',
    '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp',
    '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp'
)) {
    Copy-IntoPackage (Join-Path $fixtureProjectRoot $fixture) (Join-Path 'fixtures' (Split-Path $fixture -Leaf))
}
foreach ($animationAsset in @(
    '01-source-copies\m6-preview-animation-assets\sse\femalehead.nif',
    '01-source-copies\m6-preview-animation-assets\sse\hww0_mt_idle.hkx',
    '01-source-copies\m6-preview-animation-assets\sse\skeleton_female_sse.hkx'
)) {
    Copy-IntoPackage (Join-Path $fixtureProjectRoot $animationAsset) (Join-Path 'fixtures\m6-preview-animation-assets\sse' (Split-Path $animationAsset -Leaf))
}
foreach ($hairZapAsset in @(
    '01-source-copies\m6-preview-assets\sse\hair.nif'
)) {
    Copy-IntoPackage (Join-Path $fixtureProjectRoot $hairZapAsset) (Join-Path 'fixtures\m6-preview-hair-zap-assets\sse' (Split-Path $hairZapAsset -Leaf))
}

$schemaRoot = Join-Path $packageRoot 'schemas'
New-Item -ItemType Directory -Path $schemaRoot -Force | Out-Null
& (Join-Path $cliPublish 'npcm.exe') schema export --output (Join-Path $schemaRoot 'cli.schema.json') --json *> (Join-Path $publishRoot 'schema-export.log')
if ($LASTEXITCODE -ne 0) { throw "schema export failed with exit code $LASTEXITCODE" }

python (Join-Path $projectRoot 'tools\hardening\finalize_package.py') --package-root $packageRoot --zip $zipPath --report $reportPath --report-md $reportMdPath
if ($LASTEXITCODE -ne 0) { throw "package finalization failed with exit code $LASTEXITCODE" }
python (Join-Path $projectRoot 'tools\hardening\verify_package.py') --package-root $packageRoot --archive $zipPath --report $reportPath
if ($LASTEXITCODE -ne 0) { throw "independent package verification failed with exit code $LASTEXITCODE" }
Write-Output "PACKAGE CANDIDATE PASS $packageRoot"
