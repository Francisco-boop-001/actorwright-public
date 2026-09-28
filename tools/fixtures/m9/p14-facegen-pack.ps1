$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facegen_pack.py'
$providerFixture = Join-Path $project 'tools\fixtures\m9\p08-facegen-provider-paths.ps1'
$root = Join-Path $project '03-builds\work\m9-facegen-pack-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
& powershell -NoProfile -ExecutionPolicy Bypass -File $providerFixture | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The provider-path fixture did not pass.' }

foreach ($item in @(
    @{ Edition = 'fallout4'; Plugin = 'P08ProviderFO4.esp' },
    @{ Edition = 'skyrimse'; Plugin = 'P08ProviderSSE.esp' })) {
    $data = Join-Path $project "03-builds\work\m9-facegen-provider-paths-evidence\$($item.Edition)\Data"
    $output = Join-Path $root $item.Edition
    $response = Join-Path $root "$($item.Edition)-response.json"
    $sourcePlugin = Join-Path $data $item.Plugin
    $before = (Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash
    & $dotnet $cli facegen pack --game $item.Edition --data-root $data --plugins $item.Plugin --npc 0x800 --anchor-plugin $item.Plugin --output-root $output --json |
        Out-File -LiteralPath $response -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "FaceGen pack failed for $($item.Edition)." }
    & python $verifier --response $response --package-root $output --source-data $data --edition $item.Edition --anchor-plugin $item.Plugin
    if ($LASTEXITCODE -ne 0) { throw "Independent FaceGen package verification failed for $($item.Edition)." }
    $after = (Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash
    if ($before -ne $after) { throw "FaceGen pack changed its source plugin for $($item.Edition)." }

    $second = & $dotnet $cli facegen pack --game $item.Edition --data-root $data --plugins $item.Plugin --npc 0x800 --anchor-plugin $item.Plugin --output-root $output --json 2>$null
    if ($LASTEXITCODE -ne 4) { throw "FaceGen pack did not refuse an existing output for $($item.Edition)." }
}
Write-Output "FACEGEN PACK FIXTURES PASS $root"
