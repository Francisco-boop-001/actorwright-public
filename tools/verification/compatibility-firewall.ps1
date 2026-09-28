[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [ValidateSet('capture', 'capture-synthetic', 'verify')]
    [string]$Mode,

    [string]$DotNetPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardedArguments
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$python = (Get-Command python.exe -ErrorAction Stop).Source
$pythonVersion = (& $python --version 2>&1).ToString().Trim()
if ($pythonVersion -ne 'Python 3.12.4') {
    throw "Compatibility firewall requires pinned Python 3.12.4; found '$pythonVersion'."
}
$dotnetCandidate = if ($DotNetPath) {
    $DotNetPath
} elseif ($env:ACTORWRIGHT_TEST_DOTNET) {
    $env:ACTORWRIGHT_TEST_DOTNET
} else {
    Join-Path $repositoryRoot 'artifacts\tools\dotnet-sdk-10.0.301\dotnet.exe'
}
$dotnet = if ($dotnetCandidate) {
    (Resolve-Path -LiteralPath $dotnetCandidate -ErrorAction Stop).Path
}
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw "Pinned .NET SDK is missing: $dotnet"
}
$env:ACTORWRIGHT_TEST_DOTNET = $dotnet

Push-Location -LiteralPath $repositoryRoot
try {
    & $python -m tools.verification.compatibility_firewall $Mode @ForwardedArguments
    $firewallExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $firewallExitCode
