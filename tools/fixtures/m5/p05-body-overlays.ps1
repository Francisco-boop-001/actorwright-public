param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_body_overlays.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\m5-body-overlays-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Body-overlay evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Json([string]$path, [string]$json) {
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

$fo4 = Join-Path $output 'fo4.json'
Write-Json $fo4 '[{"template":"Tattoo.High","priority":10,"tint":[1,0.5,0,0.75],"offsetUV":[0.1,-0.2],"scaleUV":[1.2,0.8],"slots":[{"slot":3,"material":"Materials\\Actors\\Tattoo.bgem"}]},{"template":"Tattoo.Low","priority":2}]'
$fo4Response = Join-Path $output 'fo4-response.json'
& $dotnet $cli body overlay patch --game fallout4 --npc 0x800 --layers "@$fo4" --json |
    Out-File -LiteralPath $fo4Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 body-overlay proposal failed.' }
python $verifier --input $fo4 --response $fo4Response --game fallout4
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4 body-overlay verification failed.' }

$sse = Join-Path $output 'sse.json'
Write-Json $sse '[{"node":"Body [Ovl1]","diffuse":"textures\\actors\\character\\overlays\\body.dds","normal":"textures\\actors\\character\\overlays\\body_n.dds"},{"node":"Hands [Ovl0]","diffuse":"textures\\actors\\character\\overlays\\hands.dds","tint":[1,0.5,0.25,1],"alpha":0.4}]'
$sseResponse = Join-Path $output 'sse-response.json'
& $dotnet $cli body overlay patch --game skyrimse --npc 0x801 --layers "@$sse" --json |
    Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Skyrim body-overlay proposal failed.' }
python $verifier --input $sse --response $sseResponse --game skyrimse
if ($LASTEXITCODE -ne 0) { throw 'Independent Skyrim body-overlay verification failed.' }

$invalid = Join-Path $output 'invalid.json'
Write-Json $invalid '[{"node":"Body [Ovl0]","diffuse":"..\\escape.dds"}]'
& $dotnet $cli body overlay patch --game skyrimse --npc 0x800 --layers "@$invalid" --json |
    Out-File -LiteralPath (Join-Path $output 'invalid-response.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw 'Unsafe body-overlay texture did not return validation failure.' }

$mixed = Join-Path $output 'mixed.json'
Write-Json $mixed '[{"template":"Tattoo","node":"Body [Ovl0]","diffuse":"textures\\x.dds"}]'
$mixedResponse = Join-Path $output 'mixed-response.txt'
$previousErrorAction = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
& $dotnet $cli body overlay patch --game fallout4 --npc 0x800 --layers "@$mixed" --json *> $mixedResponse
$ErrorActionPreference = $previousErrorAction
if ($LASTEXITCODE -ne 2) { throw 'Cross-game body-overlay fields did not return usage failure.' }

python -m py_compile $verifier
Write-Output "BODY OVERLAY FIXTURES PASS $output"
