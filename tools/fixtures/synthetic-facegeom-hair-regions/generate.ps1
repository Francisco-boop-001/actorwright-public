[CmdletBinding()]
param(
    [switch]$Regenerate,
    [switch]$Check,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
if ($Regenerate -eq $Check) {
    throw 'Pass exactly one of -Regenerate or -Check.'
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $PSScriptRoot 'synthetic-facegeom-hair-regions-generator.csproj'
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
    & $dotnet restore $project --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw "Synthetic FaceGeom generator restore failed with exit code $LASTEXITCODE." }
    & $dotnet build $project --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "Synthetic FaceGeom generator build failed with exit code $LASTEXITCODE." }
    $generator = Join-Path $PSScriptRoot 'bin\Release\net10.0\synthetic-facegeom-hair-regions-generator.dll'
    $mode = if ($Regenerate) { '--regenerate' } else { '--check' }
    & $dotnet $generator $mode $repoRoot
    if ($LASTEXITCODE -ne 0) { throw "Synthetic FaceGeom generator $mode failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
