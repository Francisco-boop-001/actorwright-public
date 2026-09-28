param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_sse_body_weight.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\m5-sse-body-weight-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'SSE body-weight evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Json([string]$path, [string]$json) {
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

$valid = Join-Path $output 'valid.json'
Write-Json $valid '{"version":1,"game":"skyrimse","gender":"female","weightPercent":50,"weightSliderFlags":2,"baseDigit":0,"baseVertices":[[0,0,0],[1,1,1]],"twinVertices":[[2,0,0],[1,3,1]]}'
$validResponse = Join-Path $output 'valid-response.json'
& $dotnet $cli body weight resolve --game skyrimse --input $valid --json |
    Out-File -LiteralPath $validResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE body-weight resolution failed.' }
python $verifier --input $valid --response $validResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE body-weight verification failed.' }

$disabled = Join-Path $output 'disabled.json'
Write-Json $disabled '{"version":1,"game":"skyrimse","gender":"female","weightPercent":50,"weightSliderFlags":0,"baseDigit":0,"baseVertices":[[0,0,0]],"twinVertices":[[1,0,0]]}'
$disabledResponse = Join-Path $output 'disabled-response.json'
& $dotnet $cli body weight resolve --game skyrimse --input $disabled --json |
    Out-File -LiteralPath $disabledResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Disabled SSE body-weight input did not produce a safe no-op.' }
python $verifier --input $disabled --response $disabledResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent disabled body-weight verification failed.' }

$mismatch = Join-Path $output 'mismatch.json'
Write-Json $mismatch '{"version":1,"game":"skyrimse","gender":"female","weightPercent":50,"weightSliderFlags":2,"baseDigit":0,"baseVertices":[[0,0,0]],"twinVertices":[]}'
$mismatchResponse = Join-Path $output 'mismatch-refused.json'
& $dotnet $cli body weight resolve --game skyrimse --input $mismatch --json |
    Out-File -LiteralPath $mismatchResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Mismatched SSE body-weight arrays did not return validation failure.' }

$malformed = Join-Path $output 'malformed.json'
Write-Json $malformed '{"version":1}'
$malformedResponse = Join-Path $output 'malformed-refused.json'
& $dotnet $cli body weight resolve --game skyrimse --input $malformed --json |
    Out-File -LiteralPath $malformedResponse -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Malformed SSE body-weight manifest did not return validation failure.' }

python -m py_compile $verifier
Write-Output "SSE BODY WEIGHT FIXTURES PASS $output"
