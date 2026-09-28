[CmdletBinding()]
param(
    [string]$BlenderArchivePath,
    [string]$PyNiflyArchivePath,
    [string]$TexconvExecutablePath,
    [switch]$VerifyArchivesOnly
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([IO.Path]::GetPathRoot($projectRoot) -ine 'K:\') {
    throw "Renderer bootstrap requires a K: workspace: $projectRoot"
}

$toolManifestPath = Join-Path $projectRoot 'tools\manifests\external-tools.json'
$profileManifestPath = Join-Path $projectRoot 'runtime\rendering\npc-preview-profile-manifest.json'
$toolManifest = Get-Content -LiteralPath $toolManifestPath -Raw | ConvertFrom-Json
$profileManifest = Get-Content -LiteralPath $profileManifestPath -Raw | ConvertFrom-Json
$blender = @($toolManifest.tools | Where-Object name -eq 'Blender')
$pynifly = @($toolManifest.tools | Where-Object name -eq 'PyNifly Blender profile')
$texconv = @($toolManifest.tools | Where-Object name -eq 'DirectXTex texconv')
if ($blender.Count -ne 1 -or $pynifly.Count -ne 1 -or $texconv.Count -ne 1) {
    throw 'External renderer manifest is missing a unique pinned tool entry.'
}
$blender = $blender[0]
$pynifly = $pynifly[0]
$texconv = $texconv[0]
if ($profileManifest.blenderSha256 -cne $blender.verifiedArtifact.sha256) {
    throw 'Profile inventory and Blender executable pins disagree.'
}
if ($profileManifest.files.Count -ne $pynifly.verifiedArtifact.verifiedInventoryFileCount) {
    throw 'Profile inventory count differs from the external-tools manifest.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Assert-OrdinaryPathComponents([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    $diskRoot = [IO.Path]::GetPathRoot($full)
    $separators = [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $parts = $full.Substring($diskRoot.Length).Split(
        $separators,
        [System.StringSplitOptions]::RemoveEmptyEntries)
    $current = $diskRoot
    foreach ($part in $parts) {
        $current = Join-Path $current $part
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in renderer bootstrap path: $current"
        }
    }
    return $full
}

function Assert-PathBeneath([string]$path, [string]$parent) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $parentFull = [IO.Path]::GetFullPath($parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Renderer bootstrap path escaped its owned root: $full"
    }
    Assert-OrdinaryPathComponents $full | Out-Null
}

function Get-Sha256([string]$path) {
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Get-StreamSha256([System.IO.Stream]$stream) {
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($algorithm.ComputeHash($stream))
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-FilePin([string]$path, [long]$length, [string]$sha256, [string]$label) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "$label is missing: $path"
    }
    $item = Get-Item -LiteralPath $path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "$label must be an ordinary file: $path"
    }
    $actualLength = $item.Length
    if ($actualLength -ne $length) {
        throw "$label length mismatch. Expected $length, got $actualLength."
    }
    $actualHash = Get-Sha256 $path
    if ($actualHash -cne $sha256.ToUpperInvariant()) {
        throw "$label SHA-256 mismatch. Expected $sha256, got $actualHash."
    }
}

function Get-ArtifactPath(
    [string]$requestedPath,
    [string]$cacheName,
    [string]$source,
    [long]$length,
    [string]$sha256,
    [string]$label
) {
    if ([string]::IsNullOrWhiteSpace($source) -or [string]::IsNullOrWhiteSpace($sha256)) {
        throw "$label has no pinned download source or digest."
    }
    if (-not [string]::IsNullOrWhiteSpace($requestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $requestedPath).Path
        Assert-FilePin $resolved $length $sha256 "$label download"
        return $resolved
    }

    $downloadRoot = Join-Path $projectRoot 'artifacts\tools\renderer-downloads'
    Assert-OrdinaryPathComponents $downloadRoot | Out-Null
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    Assert-OrdinaryPathComponents $downloadRoot | Out-Null
    $cached = Join-Path $downloadRoot $cacheName
    if (Test-Path -LiteralPath $cached -PathType Leaf) {
        Assert-FilePin $cached $length $sha256 "$label cached download"
        return $cached
    }
    if (Test-Path -LiteralPath $cached) {
        throw "$label cache path is occupied: $cached"
    }

    $partial = "$cached.download-$([Guid]::NewGuid().ToString('N'))"
    try {
        Invoke-WebRequest -Uri $source -OutFile $partial
        Assert-FilePin $partial $length $sha256 "$label download"
        Assert-OrdinaryPathComponents $partial | Out-Null
        Assert-OrdinaryPathComponents $cached | Out-Null
        [IO.File]::Move($partial, $cached)
    }
    finally {
        if (Test-Path -LiteralPath $partial) {
            Assert-OrdinaryPathComponents $partial | Out-Null
            Remove-Item -LiteralPath $partial -Force
        }
    }
    return $cached
}

function Assert-SafeZipEntry([System.IO.Compression.ZipArchiveEntry]$entry) {
    $name = $entry.FullName
    if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\') -or $name.StartsWith('/')) {
        throw "Unsafe archive entry path: $name"
    }
    foreach ($part in $name.TrimEnd('/').Split('/')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':')) {
            throw "Unsafe archive entry path: $name"
        }
    }
    $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
    if ($unixMode -eq 0xA000) {
        throw "Symbolic link entries are not accepted: $name"
    }
}

function Test-TransientProfileEntry([string]$path) {
    return $path -match '(^|/)__pycache__(/|$)' -or
        $path.EndsWith('.pyc', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ExpectedProfileFiles {
    $expected = @{}
    $inventoryPrefix = 'scripts/addons/io_scene_nifly/'
    foreach ($record in $profileManifest.files) {
        if (-not $record.path.StartsWith($inventoryPrefix, [StringComparison]::Ordinal)) {
            throw "Unexpected inventory path: $($record.path)"
        }
        $relative = $record.path.Substring($inventoryPrefix.Length)
        $expected['io_scene_nifly/' + $relative] = $record
    }
    if ($expected.Count -ne 76) {
        throw "Expected 76 pinned PyNifly files, got $($expected.Count)."
    }
    return $expected
}

function Assert-BlenderArchive([string]$archivePath) {
    $relativeExe = $blender.verifiedArtifact.path.Substring(
        $blender.installPath.Length + 1).Replace('\', '/')
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $matches = @()
        foreach ($entry in $zip.Entries) {
            Assert-SafeZipEntry $entry
            if (-not $seen.Add($entry.FullName.TrimEnd('/'))) {
                throw "Duplicate case-insensitive Blender archive entry: $($entry.FullName)"
            }
            if ($entry.FullName -ceq $relativeExe) { $matches += $entry }
        }
        if ($matches.Count -ne 1) {
            throw "Blender archive must contain exactly one $relativeExe."
        }
        if ($matches[0].Length -ne $blender.verifiedArtifact.length) {
            throw 'Blender executable archive length differs from its pinned inventory.'
        }
        $stream = $matches[0].Open()
        try { $hash = Get-StreamSha256 $stream } finally { $stream.Dispose() }
        if ($hash -cne $blender.verifiedArtifact.sha256) {
            throw 'Blender executable in the pinned archive has the wrong SHA-256.'
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-PyNiflyArchive([string]$archivePath) {
    $expected = Get-ExpectedProfileFiles
    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $verified = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            Assert-SafeZipEntry $entry
            $canonical = $entry.FullName.TrimEnd('/')
            if (-not $seen.Add($canonical)) {
                throw "Duplicate case-insensitive PyNifly archive entry: $($entry.FullName)"
            }
            if ($entry.FullName.EndsWith('/')) {
                if ($entry.FullName -ne 'io_scene_nifly/' -and
                    -not $entry.FullName.StartsWith('io_scene_nifly/', [StringComparison]::Ordinal)) {
                    throw "Unexpected PyNifly directory: $($entry.FullName)"
                }
                continue
            }
            if (-not $entry.FullName.StartsWith('io_scene_nifly/', [StringComparison]::Ordinal)) {
                throw "Unexpected PyNifly archive file: $($entry.FullName)"
            }
            $relative = $entry.FullName.Substring('io_scene_nifly/'.Length)
            if ($expected.ContainsKey($entry.FullName)) {
                $record = $expected[$entry.FullName]
                if ($entry.Length -ne $record.length) {
                    throw "PyNifly file length mismatch: $($record.path)"
                }
                $stream = $entry.Open()
                try { $hash = Get-StreamSha256 $stream } finally { $stream.Dispose() }
                if ($hash -cne $record.sha256) {
                    throw "PyNifly file SHA-256 mismatch: $($record.path)"
                }
                [void]$verified.Add($entry.FullName)
                continue
            }
            if (Test-TransientProfileEntry $entry.FullName) { continue }
            throw "Unlisted non-transient PyNifly archive file: $($entry.FullName)"
        }
        if ($verified.Count -ne $expected.Count) {
            throw "PyNifly archive has $($verified.Count) of $($expected.Count) inventory files."
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Resolve-WorkspaceRelativePath([string]$relativePath) {
    if ([IO.Path]::IsPathRooted($relativePath)) {
        throw "Manifest path is not workspace-relative: $relativePath"
    }
    foreach ($part in $relativePath.Replace('\', '/').Split('/')) {
        if ([string]::IsNullOrWhiteSpace($part) -or $part -eq '.' -or $part -eq '..' -or $part.Contains(':')) {
            throw "Manifest path is not workspace-relative: $relativePath"
        }
    }
    $full = [IO.Path]::GetFullPath((Join-Path $projectRoot $relativePath))
    Assert-OrdinaryPathComponents $full | Out-Null
    return $full
}

function Assert-InstalledProfile([string]$profileRoot) {
    $expected = Get-ExpectedProfileFiles
    $addonRoot = Join-Path $profileRoot 'scripts\addons\io_scene_nifly'
    $expectedRelative = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($inventoryPath in $expected.Keys) {
        $relative = $inventoryPath.Substring('io_scene_nifly/'.Length)
        [void]$expectedRelative.Add($relative)
        $record = $expected[$inventoryPath]
        Assert-FilePin (Join-Path $addonRoot $relative.Replace('/', '\')) $record.length $record.sha256 $record.path
    }
    $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if (Test-Path -LiteralPath $addonRoot) {
        Get-ChildItem -LiteralPath $addonRoot -Force -Recurse | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Reparse point in installed PyNifly profile: $($_.FullName)"
            }
            if (-not $_.PSIsContainer) {
                $relative = [IO.Path]::GetRelativePath($addonRoot, $_.FullName).Replace('\', '/')
                if (-not (Test-TransientProfileEntry $relative)) { [void]$actual.Add($relative) }
            }
        }
    }
    if ($actual.Count -ne $expectedRelative.Count) {
        throw "Installed PyNifly file count mismatch: expected $($expectedRelative.Count), got $($actual.Count)."
    }
    foreach ($relative in $expectedRelative) {
        if (-not $actual.Contains($relative)) { throw "Installed PyNifly file is missing: $relative" }
    }
    foreach ($directory in @('config', 'data')) {
        $path = Join-Path $profileRoot $directory
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Installed PyNifly profile is missing $directory."
        }
        if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Reparse point in installed PyNifly profile: $path"
        }
    }
}

Assert-OrdinaryPathComponents $projectRoot | Out-Null
$blenderArchive = Get-ArtifactPath $BlenderArchivePath 'blender-4.5.1-windows-x64.zip' $blender.downloadArtifactUrl $blender.archiveLength $blender.archiveSha256 'Blender'
$pyniflyArchive = Get-ArtifactPath $PyNiflyArchivePath 'io_scene_nifly-27.4.0.zip' $pynifly.downloadArtifactUrl $pynifly.archiveLength $pynifly.archiveSha256 'PyNifly'
$texconvDownload = Get-ArtifactPath $TexconvExecutablePath 'texconv-2026.5.7.exe' $texconv.downloadArtifactUrl $texconv.downloadArtifactLength $texconv.downloadArtifactSha256 'texconv'
Assert-BlenderArchive $blenderArchive
Assert-PyNiflyArchive $pyniflyArchive
Write-Output "Verified Blender archive SHA-256: $(Get-Sha256 $blenderArchive)"
Write-Output "Verified PyNifly archive SHA-256: $(Get-Sha256 $pyniflyArchive)"
Write-Output "Verified texconv release executable SHA-256: $(Get-Sha256 $texconvDownload)"
if ($VerifyArchivesOnly) {
    Write-Output 'Pinned renderer downloads and inventory verified; no files installed.'
    return
}

$externalRoot = Resolve-WorkspaceRelativePath 'tools/external'
$targets = @(
    @{ Name = 'Blender'; Target = (Resolve-WorkspaceRelativePath $blender.installPath) },
    @{ Name = 'PyNifly'; Target = (Resolve-WorkspaceRelativePath $pynifly.installPath) },
    @{ Name = 'texconv'; Target = (Resolve-WorkspaceRelativePath $texconv.installPath) }
)
Assert-OrdinaryPathComponents $externalRoot | Out-Null
New-Item -ItemType Directory -Path $externalRoot -Force | Out-Null
Assert-OrdinaryPathComponents $externalRoot | Out-Null
$exists = @($targets | ForEach-Object { Test-Path -LiteralPath $_.Target })
if (@($exists | Where-Object { $_ }).Count -gt 0) {
    if (@($exists | Where-Object { -not $_ }).Count -gt 0) {
        throw 'A partial renderer install exists; refusing to replace or complete it.'
    }
    $blenderExe = Join-Path $targets[0].Target $blender.verifiedArtifact.path.Substring(
        $blender.installPath.Length + 1).Replace('/', '\')
    Assert-FilePin $blenderExe $blender.verifiedArtifact.length $blender.verifiedArtifact.sha256 'Installed Blender executable'
    Assert-InstalledProfile $targets[1].Target
    Assert-FilePin (Join-Path $targets[2].Target 'texconv.exe') $texconv.verifiedArtifact.length $texconv.verifiedArtifact.sha256 'Installed texconv'
    Write-Output 'Existing pinned Blender, PyNifly, and texconv installs match the committed inventory.'
    return
}

$stageRoot = Join-Path $externalRoot ".renderer-bootstrap-$([Guid]::NewGuid().ToString('N'))"
$stagePaths = @(
    (Join-Path $stageRoot 'blender'),
    (Join-Path $stageRoot 'profile'),
    (Join-Path $stageRoot 'texconv')
)
Assert-PathBeneath $stageRoot $externalRoot
try {
    Assert-PathBeneath $stageRoot $externalRoot
    New-Item -ItemType Directory -Path $stagePaths -Force | Out-Null
    Assert-PathBeneath $stageRoot $externalRoot
    [System.IO.Compression.ZipFile]::ExtractToDirectory($blenderArchive, $stagePaths[0])
    $expected = Get-ExpectedProfileFiles
    $zip = [System.IO.Compression.ZipFile]::OpenRead($pyniflyArchive)
    try {
        foreach ($inventoryPath in $expected.Keys) {
            $entry = $zip.GetEntry($inventoryPath)
            if ($null -eq $entry) { throw "Verified PyNifly archive entry disappeared: $inventoryPath" }
            $relative = $inventoryPath.Substring('io_scene_nifly/'.Length).Replace('/', '\')
            $destination = Join-Path $stagePaths[1] (Join-Path 'scripts\addons\io_scene_nifly' $relative)
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Assert-PathBeneath $destination $stageRoot
            $archiveStream = $entry.Open()
            try {
                $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $archiveStream.CopyTo($output) } finally { $output.Dispose() }
            }
            finally { $archiveStream.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    New-Item -ItemType Directory -Path (Join-Path $stagePaths[1] 'config'), (Join-Path $stagePaths[1] 'data') -Force | Out-Null
    Copy-Item -LiteralPath $texconvDownload -Destination (Join-Path $stagePaths[2] 'texconv.exe')

    $blenderExeRelative = $blender.verifiedArtifact.path.Substring(
        $blender.installPath.Length + 1).Replace('/', '\')
    Assert-FilePin (Join-Path $stagePaths[0] $blenderExeRelative) $blender.verifiedArtifact.length $blender.verifiedArtifact.sha256 'Staged Blender executable'
    Assert-InstalledProfile $stagePaths[1]
    Assert-FilePin (Join-Path $stagePaths[2] 'texconv.exe') $texconv.verifiedArtifact.length $texconv.verifiedArtifact.sha256 'Staged texconv'

    for ($index = 0; $index -lt $targets.Count; $index++) {
        Assert-PathBeneath $stagePaths[$index] $externalRoot
        Assert-OrdinaryPathComponents $targets[$index].Target | Out-Null
        if (Test-Path -LiteralPath $targets[$index].Target) {
            throw "Renderer target became occupied before publication: $($targets[$index].Target)"
        }
        [IO.Directory]::Move($stagePaths[$index], $targets[$index].Target)
    }
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Assert-PathBeneath $stageRoot $externalRoot
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}

$blenderExe = Join-Path $targets[0].Target $blender.verifiedArtifact.path.Substring(
    $blender.installPath.Length + 1).Replace('/', '\')
Assert-FilePin $blenderExe $blender.verifiedArtifact.length $blender.verifiedArtifact.sha256 'Installed Blender executable'
Assert-InstalledProfile $targets[1].Target
Assert-FilePin (Join-Path $targets[2].Target 'texconv.exe') $texconv.verifiedArtifact.length $texconv.verifiedArtifact.sha256 'Installed texconv'
Write-Output "Installed full Blender distribution: $($targets[0].Target)"
Write-Output "Installed verified PyNifly profile: $($targets[1].Target)"
Write-Output "Installed verified texconv executable: $($targets[2].Target)"
