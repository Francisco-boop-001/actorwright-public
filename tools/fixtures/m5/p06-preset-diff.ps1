$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m4-preset-diff-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preset_diff.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$fo4Left = Join-Path $root 'fo4-left.json'
$fo4Right = Join-Path $root 'fo4-right.json'
$fo4Response = Join-Path $root 'fo4-response.json'
$fo4Repeat = Join-Path $root 'fo4-repeat.json'
$fo4Fixture = Join-Path $project '01-source-copies\m4-fixtures\fo4-looksmenu.json'
$fo4Text = [IO.File]::ReadAllText($fo4Fixture)
[IO.File]::WriteAllText($fo4Left, $fo4Text, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($fo4Right, $fo4Text.Replace('"CBBE Breast":0.25', '"CBBE Breast":0.35').Replace('"Percent":1', '"Percent":99').Replace('"Gender":1', '"Gender":2'), (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset diff --format looksmenu --edition fallout4 --left $fo4Left --right $fo4Right --json | Out-File -LiteralPath $fo4Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FO4 preset diff failed' }
& $dotnet $cli preset diff --format looksmenu --edition fallout4 --left $fo4Left --right $fo4Right --json | Out-File -LiteralPath $fo4Repeat -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Repeated FO4 preset diff failed' }
& python $verifier --format looksmenu --left $fo4Left --right $fo4Right --response $fo4Response --repeat $fo4Repeat
if ($LASTEXITCODE -ne 0) { throw 'Independent FO4 preset diff verification failed' }

$sseLeft = Join-Path $root 'sse-left.jslot'
$sseRight = Join-Path $root 'sse-right.jslot'
$sseResponse = Join-Path $root 'sse-response.json'
$sseRepeat = Join-Path $root 'sse-repeat.json'
$sseFixture = Join-Path $project '01-source-copies\m4-fixtures\sse-racemenu.jslot'
$sseText = [IO.File]::ReadAllText($sseFixture)
[IO.File]::WriteAllText($sseLeft, $sseText, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($sseRight, $sseText.Replace('0.4', '0.45').Replace('-0.2', '-0.25').Replace('{"headParts"', '{"futureField":true,"headParts"').Replace('Skyrim.esm|00012345', 'unresolved'), (New-Object Text.UTF8Encoding($false)))
& $dotnet $cli preset diff --format racemenu-jslot --edition skyrimse --left $sseLeft --right $sseRight --json | Out-File -LiteralPath $sseResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'SSE preset diff failed' }
& $dotnet $cli preset diff --format racemenu-jslot --edition skyrimse --left $sseLeft --right $sseRight --json | Out-File -LiteralPath $sseRepeat -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Repeated SSE preset diff failed' }
& python $verifier --format racemenu-jslot --left $sseLeft --right $sseRight --response $sseResponse --repeat $sseRepeat
if ($LASTEXITCODE -ne 0) { throw 'Independent SSE preset diff verification failed' }

$outsideResponse = Join-Path $root 'outside-response.json'
& $dotnet $cli preset diff --format looksmenu --edition fallout4 --left $fo4Left --right 'F:\ExampleGame\Data\missing.json' --json | Out-File -LiteralPath $outsideResponse -Encoding utf8
if ($LASTEXITCODE -eq 0) { throw 'Protected-root preset diff input was accepted' }

Write-Output "PRESET DIFF FIXTURES PASS $root"
