param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [Parameter(Mandatory = $true)][string]$PythonPath,
    [Parameter(Mandatory = $true)][string]$GitPath,
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Commit,
    [Parameter(Mandatory = $true)][string]$Version
)

$ErrorActionPreference = 'Stop'
$savedErrorActionPreference = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    # This one verifier call creates the deterministic ZIP and runs the sealed
    # package compatibility overlap before it can return success to the tag step.
    $verificationOutput = @(& $PythonPath (Join-Path $PSScriptRoot 'verify_release.py') `
        $OutputRoot --create-zip $ZipPath --json 2>&1 |
        ForEach-Object { $_.ToString() })
    $verificationExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if ($verificationExitCode -ne 0) {
    throw "Release verification failed with exit code $verificationExitCode"
}
$verificationOutput | Write-Output
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
    throw "Release verification did not emit a valid sealed compatibility result"
}
if ($verificationResult -isnot [pscustomobject] -or
    $verificationResult.status -isnot [string] -or
    $verificationResult.status -cne 'PASS' -or
    $verificationResult.zipVerified -isnot [bool] -or
    $verificationResult.zipVerified -ne $true -or
    $verificationResult.compatibilityVerdict -isnot [string] -or
    $verificationResult.compatibilityVerdict -cne 'FULL_COMPATIBLE') {
    throw "Release verification did not establish FULL_COMPATIBLE sealed bytes"
}
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw "Release commit must be an exact 40-character Git commit ID"
}
$resolvedCommit = (& $GitPath -c core.fsmonitor=false -C $ProjectRoot `
    rev-parse --verify "$Commit^{commit}" 2>$null)
if ($LASTEXITCODE -ne 0 -or @($resolvedCommit).Count -ne 1 -or
    $resolvedCommit.Trim() -ine $Commit) {
    throw "Release commit does not resolve to the exact requested commit"
}
if ($verificationResult.sourceCommit -isnot [string] -or
    $verificationResult.sourceCommit -notmatch '^[0-9a-fA-F]{40}$' -or
    $verificationResult.sourceCommit -ine $resolvedCommit.Trim()) {
    throw "Release verification source commit differs from the requested commit"
}
if ($verificationResult.version -isnot [string] -or
    $verificationResult.version -cne $Version -or $Tag -cne "v$Version") {
    throw "Release verification version and tag differ from the requested release"
}

function Assert-FullFirewallReport([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or
        -not ($item -is [IO.FileInfo]) -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Full compatibility firewall report is missing, empty, or not an ordinary file: $Path"
    }
}

$firewallScript = Join-Path $ProjectRoot 'tools\verification\compatibility-firewall.ps1'
$firewallScriptItem = Get-Item -LiteralPath $firewallScript -Force -ErrorAction SilentlyContinue
if ($null -eq $firewallScriptItem -or
    -not ($firewallScriptItem -is [IO.FileInfo]) -or
    ($firewallScriptItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Full compatibility firewall script is missing or not an ordinary file: $firewallScript"
}
$firewallReportRoot = Join-Path $ProjectRoot 'artifacts\test-work'
New-Item -ItemType Directory -Force -Path $firewallReportRoot | Out-Null
$firewallReportRootItem = Get-Item -LiteralPath $firewallReportRoot -Force
if (-not ($firewallReportRootItem -is [IO.DirectoryInfo]) -or
    ($firewallReportRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Full compatibility report root is not an ordinary directory: $firewallReportRoot"
}
$firewallReport = Join-Path $firewallReportRoot (
    'compatibility-firewall-full-' + [Guid]::NewGuid().ToString('N') + '.json')
$sourceCli = Join-Path $ProjectRoot 'src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe'
try {
    $ErrorActionPreference = 'Continue'
    $firewallOutput = @(& $firewallScript verify `
        -Tier Full `
        -Baseline publicSynthetic `
        -SourceCli $sourceCli `
        -ReleaseRoot $OutputRoot `
        -ReleaseZip $ZipPath `
        -Output $firewallReport 2>&1 | ForEach-Object { $_.ToString() })
    $firewallExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
if ($firewallExitCode -ne 0) {
    throw "Full compatibility firewall failed with exit code $firewallExitCode"
}
Assert-FullFirewallReport -Path $firewallReport
try {
    $rawFirewallJson = Get-Content -LiteralPath $firewallReport -Raw -Encoding UTF8
    if (-not $rawFirewallJson.TrimStart().StartsWith('{')) {
        throw 'Full firewall JSON root must be an object'
    }
    $firewallResult = $rawFirewallJson | ConvertFrom-Json -ErrorAction Stop
} catch {
    throw "Full compatibility firewall report is not valid JSON: $firewallReport"
}
if ($firewallResult -isnot [pscustomobject] -or
    $firewallResult.tier -isnot [string] -or
    $firewallResult.tier -cne 'Full' -or
    $firewallResult.baselineId -isnot [string] -or
    $firewallResult.baselineId -cne 'publicSynthetic' -or
    $firewallResult.compatible -isnot [bool] -or
    $firewallResult.compatible -ne $true -or
    $firewallResult.verdict -isnot [string] -or
    $firewallResult.verdict -cne 'FULL_COMPATIBLE' -or
    $firewallResult.fixtureBacked -isnot [bool] -or
    $firewallResult.fixtureBacked -ne $false -or
    $firewallResult.realInstallation -isnot [bool] -or
    $firewallResult.realInstallation -ne $false) {
    throw "Full compatibility firewall did not establish FULL_COMPATIBLE release bytes"
}
$firewallOutput | Write-Output

try {
    $ErrorActionPreference = 'Continue'
    $tagOutput = & $GitPath -c core.fsmonitor=false -C $ProjectRoot `
        tag -a $Tag $Commit -m "Actorwright $Version" 2>&1 |
        ForEach-Object { $_.ToString() }
    $tagExitCode = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $savedErrorActionPreference
}
$tagOutput | Write-Output
if ($tagExitCode -ne 0) {
    throw "Annotated release tag creation failed with exit code $tagExitCode`: $Tag"
}
$taggedCommit = (& $GitPath -c core.fsmonitor=false -C $ProjectRoot `
    rev-parse "$Tag^{}" 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $taggedCommit -ine $resolvedCommit.Trim()) {
    throw "Annotated release tag does not peel to the verified source commit: $Tag"
}
