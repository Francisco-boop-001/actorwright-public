[CmdletBinding()]
param(
    [switch]$Regenerate,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
if (-not $Regenerate) {
    throw 'Pass -Regenerate to overwrite the two synthetic topology ESP fixtures.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $PSScriptRoot 'topology-generator.csproj'
$output = Join-Path $repoRoot 'tests\fixtures\skyrim-plugin-topology'
if (-not $DotNetPath) {
    $DotNetPath = [Environment]::GetEnvironmentVariable('ACTORWRIGHT_TEST_DOTNET')
}
if ($DotNetPath) {
    $dotnet = (Resolve-Path -LiteralPath $DotNetPath -ErrorAction Stop).Path
}
else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$globalJson = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
$pinnedSdkVersion = [string]$globalJson.sdk.version
Push-Location $repoRoot
try {
    $selectedSdkVersion = (& $dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $selectedSdkVersion -ne $pinnedSdkVersion) {
        throw "Pinned .NET SDK version mismatch: expected $pinnedSdkVersion; observed '$selectedSdkVersion'."
    }
    & $dotnet restore $project --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Topology fixture restore failed with exit code $LASTEXITCODE." }
    & $dotnet build $project --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Topology fixture build failed with exit code $LASTEXITCODE." }
    $generator = Join-Path $PSScriptRoot 'bin\Release\net10.0\topology-generator.dll'
    & $dotnet $generator $output
    if ($LASTEXITCODE -ne 0) { throw "Topology fixture generation failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}