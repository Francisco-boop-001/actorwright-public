$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$root = Join-Path $project '03-builds\work\m5-appearance-copy-evidence'
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_appearance_copy.py'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null

$source = Join-Path $root 'source.json'
$target = Join-Path $root 'target.json'
$output = Join-Path $root 'output.json'
$response = Join-Path $root 'response.json'
$inspect = Join-Path $root 'inspect.json'
$fixture = Join-Path $project '01-source-copies\m4-fixtures\fo4-looksmenu.json'
$sourceText = [IO.File]::ReadAllText($fixture)
[IO.File]::WriteAllText($source, $sourceText, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText($target, $sourceText.Replace('"Weight":[0.2,0.5,0.3]', '"Weight":[0.8,0.7,0.6]').Replace('"Percent":1', '"Percent":99'), (New-Object Text.UTF8Encoding($false)))

& $dotnet $cli appearance copy --format looksmenu --edition fallout4 --from $source --to $target --sections body-weight,face-tints --output $output --json | Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'selected appearance copy failed' }
& $dotnet $cli preset inspect --format looksmenu --edition fallout4 --input $output --json | Out-File -LiteralPath $inspect -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'copied appearance output did not reload' }
& python $verifier --source $source --target $target --response $response --inspect $inspect
if ($LASTEXITCODE -ne 0) { throw 'independent appearance-copy verification failed' }

$unsupportedOutput = Join-Path $root 'unsupported.json'
& $dotnet $cli appearance copy --format looksmenu --edition fallout4 --from $source --to $target --sections sculpt --output $unsupportedOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $unsupportedOutput)) { throw 'cross-game sculpt copy was accepted' }

$emptyOutput = Join-Path $root 'empty.json'
& $dotnet $cli appearance copy --format looksmenu --edition fallout4 --from $source --to $target --sections ',' --output $emptyOutput --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $emptyOutput)) { throw 'empty section selection was accepted' }

Write-Output "APPEARANCE COPY FIXTURES PASS $root"
