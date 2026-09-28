$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_package_commands.py'
$root = Join-Path $project '03-builds\work\m9-package-commands-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$source = Join-Path $root 'source'
$output = Join-Path $root 'output'
New-Item -ItemType Directory -Force -Path (Join-Path $source 'Data') | Out-Null
[byte[]]$plugin = 1,2,3,4,5
[IO.File]::WriteAllBytes((Join-Path $source 'Data\P15Package.esp'), $plugin)
$hash = (Get-FileHash -LiteralPath (Join-Path $source 'Data\P15Package.esp') -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schemaVersion = 1; edition = 'fallout4'; presetFormat = 'LooksMenu'; sourcePreset = 'P15.json'
    sourcePresetSha256 = ('0' * 64); sourcePlugin = 'P15Source.esp'; sourcePluginSha256 = ('1' * 64)
    outputPlugin = 'P15Package.esp'; targetFormId = '0x800'; artifacts = @(@{
        kind = 'plugin'; relativePath = 'Data/P15Package.esp'; byteLength = $plugin.Length; sha256 = $hash
    })
}
$manifestPath = Join-Path $source 'npcmanager-package.json'
$manifestJson = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $manifestJson, [Text.UTF8Encoding]::new($false))
$inspect = Join-Path $root 'inspect.json'
& $dotnet $cli package inspect --manifest $manifestPath --json | Set-Content -LiteralPath $inspect -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'package inspect failed.' }
$verify = Join-Path $root 'verify.json'
& $dotnet $cli package verify --manifest $manifestPath --json | Set-Content -LiteralPath $verify -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'package verify failed.' }
& python $verifier --manifest $manifestPath
if ($LASTEXITCODE -ne 0) { throw 'Independent package verifier failed.' }
& $dotnet $cli package build --source-root $source --output-root $output --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'package build failed.' }
& python $verifier --manifest (Join-Path $output 'npcmanager-package.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent output package verifier failed.' }
[IO.File]::WriteAllBytes((Join-Path $output 'Data\P15Package.esp'), [byte[]](9,9,9))
$outputManifest = Join-Path $output 'npcmanager-package.json'
& $dotnet $cli package verify --manifest $outputManifest --json | Out-Null
if ($LASTEXITCODE -ne 4) { throw 'Tampered package was not rejected.' }
Write-Output "PACKAGE COMMAND FIXTURE PASS $root"
