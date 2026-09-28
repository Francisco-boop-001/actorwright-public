$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $root 'tools\build\build.ps1') -Configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
