param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,
    [Parameter(Mandatory = $true)]
    [string]$VerificationJson,
    [ValidateSet('None', 'FailBeforePublish', 'AttemptManifestSwap')]
    [string]$TestHook = 'None'
)

$ErrorActionPreference = 'Stop'

try {
    $verification = $VerificationJson | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw 'The package verifier result is not complete valid JSON.'
}
if ($verification -isnot [PSCustomObject] -or
    $verification.status -isnot [string] -or
    $verification.status -cne 'PASS' -or
    $verification.protocolV2Kernel -isnot [bool] -or
    $verification.protocolV2Kernel -ne $true) {
    throw 'Durable evidence requires status PASS and protocolV2Kernel true.'
}

# Persist the exact deterministic representation that is hashed. Consumers can
# recompute verifierResultSha256 directly from verifierResultJson as UTF-8.
$canonicalVerificationJson = [string](
    $verification | ConvertTo-Json -Depth 100 -Compress)
$canonicalVerificationJson = $canonicalVerificationJson -replace "`r`n", "`n"

$helperPath = Join-Path $PSScriptRoot 'PackageVerificationEvidenceFileSystem.cs'
if (-not ('Actorwright.Build.PackageVerificationEvidenceFileSystem' -as [type])) {
    Add-Type -Path $helperPath -ErrorAction Stop
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$destination = [Actorwright.Build.PackageVerificationEvidenceFileSystem]::Write(
    $projectRoot,
    $PackageRoot,
    $canonicalVerificationJson,
    $TestHook)
Write-Output $destination
