[CmdletBinding()]
param(
    [string]$WorkspaceRoot = 'K:\ExampleWorkspace'
)

$ErrorActionPreference = 'Stop'

$bundle = Join-Path $WorkspaceRoot 'tools\external\_downloads\vc_redist.x64.exe'
$layoutRoot = Join-Path $WorkspaceRoot 'tools\external\microsoft-vc-redist-14.51.36247.0-x64\layout'
$containerRoot = Join-Path $layoutRoot 'containers'
$expectedBundleLength = 18731856L
$expectedBundleSha256 = '843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C'

$containers = @(
    @{
        Name = 'burn-ui'
        Offset = 479744L
        Length = 140070L
        ExpectedFiles = 19
    }
    @{
        Name = 'runtime-payloads'
        Offset = 630000L
        Length = 18091661L
        ExpectedFiles = 6
    }
)

function Copy-ExactRange {
    param(
        [System.IO.Stream]$InputStream,
        [long]$Offset,
        [long]$Length,
        [string]$Destination
    )

    $InputStream.Position = $Offset
    $output = [System.IO.File]::Open(
        $Destination,
        [System.IO.FileMode]::Create,
        [System.IO.FileAccess]::Write,
        [System.IO.FileShare]::None)
    try {
        $remaining = $Length
        $buffer = New-Object byte[] 1048576
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min($buffer.Length, $remaining)
            $read = $InputStream.Read($buffer, 0, $requested)
            if ($read -le 0) {
                throw "Unexpected end of bundle while extracting $Destination."
            }

            $output.Write($buffer, 0, $read)
            $remaining -= $read
        }
    }
    finally {
        $output.Dispose()
    }
}

$bundleItem = Get-Item -LiteralPath $bundle
if ($bundleItem.Length -ne $expectedBundleLength) {
    throw "vc-redist-bundle-length-mismatch"
}

$bundleHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $bundle).Hash
if ($bundleHash -ne $expectedBundleSha256) {
    throw "vc-redist-bundle-sha256-mismatch"
}

New-Item -ItemType Directory -Path $containerRoot -Force | Out-Null
$input = [System.IO.File]::OpenRead($bundle)
$reader = New-Object System.IO.BinaryReader($input)
try {
    foreach ($container in $containers) {
        $input.Position = $container.Offset
        $signature = [System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if ($signature -ne 'MSCF') {
            throw "vc-redist-container-signature-mismatch:$($container.Name)"
        }

        $input.Position = $container.Offset + 8
        $declaredLength = $reader.ReadUInt32()
        $input.Position = $container.Offset + 26
        $folderCount = $reader.ReadUInt16()
        $fileCount = $reader.ReadUInt16()
        if ($declaredLength -ne $container.Length -or
            $folderCount -ne 1 -or
            $fileCount -ne $container.ExpectedFiles) {
            throw "vc-redist-container-header-mismatch:$($container.Name)"
        }

        $cabPath = Join-Path $containerRoot "$($container.Name).cab"
        Copy-ExactRange -InputStream $input -Offset $container.Offset `
            -Length $container.Length -Destination $cabPath

        $expandedRoot = Join-Path $layoutRoot $container.Name
        New-Item -ItemType Directory -Path $expandedRoot -Force | Out-Null
        & "$env:SystemRoot\System32\expand.exe" '-F:*' $cabPath $expandedRoot
        if ($LASTEXITCODE -ne 0) {
            throw "vc-redist-container-expand-failed:$($container.Name)"
        }
    }
}
finally {
    $reader.Dispose()
    $input.Dispose()
}

Get-ChildItem -LiteralPath $layoutRoot -Recurse -File |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName.Substring($WorkspaceRoot.Length + 1)
            Length = $_.Length
            Sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        }
    }
