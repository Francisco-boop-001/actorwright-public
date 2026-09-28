param()

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$archive = Join-Path $projectRoot 'artifacts\tools\dotnet-sdk-10.0.301-win-x64.zip'
$installRoot = Join-Path $projectRoot 'artifacts\tools\dotnet-sdk-10.0.301'
$manifest = Join-Path $projectRoot 'tools\manifests\dotnet-sdk-10.0.301-win-x64.json'

$pins = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json

function Get-FileDigest([string]$path, [string]$algorithm) {
    $hasher = [System.Security.Cryptography.HashAlgorithm]::Create($algorithm)
    try {
        $stream = [System.IO.File]::OpenRead($path)
        try {
            return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $hasher.Dispose()
    }
}

function Assert-DotNetExecutable([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "SDK executable is missing: $path"
    }
    $length = (Get-Item -LiteralPath $path).Length
    if ($length -ne $pins.dotnet_length) {
        throw "SDK executable length mismatch. Expected $($pins.dotnet_length), got $length."
    }
    $hash = Get-FileDigest $path 'SHA256'
    if ($hash -ne $pins.dotnet_sha256.ToLowerInvariant()) {
        throw "SDK executable hash mismatch. Expected $($pins.dotnet_sha256), got $hash."
    }
    $version = (& $path --version 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -ne '10.0.301') {
        throw "SDK executable version mismatch. Expected 10.0.301, got '$version'."
    }
}

$dotnet = Join-Path $installRoot 'dotnet.exe'
$installValid = $false
if (Test-Path -LiteralPath $dotnet -PathType Leaf) {
    try {
        Assert-DotNetExecutable $dotnet
        $installValid = $true
    }
    catch {
        $installValid = $false
    }
}
if (-not $installValid) {
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "Pinned SDK archive is missing: $archive"
    }
    $expected = $pins.archive_sha512.ToLowerInvariant()
    $actual = Get-FileDigest $archive 'SHA512'
    if ($actual -ne $expected) {
        throw "Pinned SDK archive hash mismatch. Expected $expected, got $actual."
    }
    $stagingRoot = "$installRoot.stage-$([Guid]::NewGuid().ToString('N'))"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $stagingRoot)
        $stagingDotnet = Join-Path $stagingRoot 'dotnet.exe'
        Assert-DotNetExecutable $stagingDotnet
        if (Test-Path -LiteralPath $installRoot) {
            Remove-Item -LiteralPath $installRoot -Recurse -Force
        }
        Move-Item -LiteralPath $stagingRoot -Destination $installRoot
    }
    finally {
        if (Test-Path -LiteralPath $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
    }
}

$env:DOTNET_ROOT = $installRoot
$env:PATH = "$installRoot;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$version = (& $dotnet --version).Trim()
if ($version -ne '10.0.301') {
    throw "Unexpected local SDK version '$version'."
}
Write-Output "Pinned .NET SDK ready: $dotnet ($version)"
