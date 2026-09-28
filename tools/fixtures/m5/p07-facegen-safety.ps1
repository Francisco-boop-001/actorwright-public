$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-facegen-safety-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_manifests.py'
$valid = Join-Path $project '01-source-copies\m5-fixtures\fo4-facegen-valid.json'
$zero = Join-Path $project '01-source-copies\m5-fixtures\zero-shapes.json'
$poison = Join-Path $project '01-source-copies\m5-fixtures\poison-shapes.json'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Invoke-Checked([string]$name, [string[]]$arguments, [int]$expectedExit = 0) {
    $response = Join-Path $root "$name.json"
    & $dotnet $cli @arguments --json | Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne $expectedExit) { throw "$name returned $LASTEXITCODE; expected $expectedExit" }
}

Invoke-Checked 'diagnose-valid' @('facegen', 'diagnose', '--edition', 'fallout4', '--manifest', $valid, '--npc', '0x800')
Invoke-Checked 'verify-valid' @('facegen', 'verify', '--edition', 'fallout4', '--manifest', $valid, '--npc', '0x800', '--strict-shapes')
Invoke-Checked 'verify-zero-refusal' @('facegen', 'verify', '--edition', 'fallout4', '--manifest', $zero, '--npc', '0x800', '--strict-shapes') 4
Invoke-Checked 'verify-poison-refusal' @('facegen', 'verify', '--edition', 'fallout4', '--manifest', $poison, '--npc', '0x800', '--strict-shapes') 4
& python $verifier
if ($LASTEXITCODE -ne 0) { throw 'Independent FaceGen manifest verifier failed.' }
Write-Output "FACEGEN SAFETY FIXTURES PASS $root"
