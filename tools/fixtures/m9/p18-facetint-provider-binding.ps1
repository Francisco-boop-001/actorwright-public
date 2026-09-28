$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$workspace = (Resolve-Path "$project\..\..").Path
$root = Join-Path $project '03-builds\work\m9-face-tint-provider-evidence'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$nuget = Join-Path $workspace 'tools\external\dotnet-cli-home\.nuget\packages'
$fixtureProject = Join-Path $project 'tools\fixtures\m2\fixture-generator.csproj'
$fixtureGenerator = Join-Path $project 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_facetint_provider_binding.py'
$report = Join-Path $project '05-reports\m9-facetint-provider-binding-2026-07-19ag.json'
$plugin = 'P18Provider.esp'
$data = Join-Path $root 'Data'
$response = Join-Path $root 'resolve.json'

if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root, $data | Out-Null
$env:NUGET_PACKAGES = $nuget
$env:DOTNET_CLI_HOME = (Join-Path $workspace 'tools\external\dotnet-cli-home')

& $dotnet build $fixtureProject --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'FaceTint fixture generator build failed.' }
& $dotnet $fixtureGenerator '--face-tint-provider' (Join-Path $root $plugin)
if ($LASTEXITCODE -ne 0) { throw 'FaceTint fixture plugin generation failed.' }
Copy-Item -LiteralPath (Join-Path $root $plugin) -Destination (Join-Path $data $plugin)

$geom = Join-Path $data "Meshes\Actors\Character\FaceGenData\FaceGeom\$plugin"
$custom = Join-Path $data "Textures\Actors\Character\FaceCustomization\$plugin"
New-Item -ItemType Directory -Force -Path $geom, $custom | Out-Null
Set-Content -LiteralPath (Join-Path $geom '00000800.nif') -Value 'facegeom-p18' -NoNewline
foreach ($suffix in @('_d', '_msn', '_s')) {
    Set-Content -LiteralPath (Join-Path $custom "00000800$suffix.dds") -Value "custom$suffix" -NoNewline
}

& $dotnet $cli facegen resolve-providers --game fallout4 --data-root $data --plugins $plugin --npc 0x800 --json |
    Out-File -LiteralPath $response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'FaceTint provider resolution failed.' }
& python $verifier --json $response --data-root $data --plugin $plugin
if ($LASTEXITCODE -ne 0) { throw 'Independent FaceTint provider verification failed.' }

$responseHash = (Get-FileHash -LiteralPath $response -Algorithm SHA256).Hash.ToLowerInvariant()
$pluginHash = (Get-FileHash -LiteralPath (Join-Path $data $plugin) -Algorithm SHA256).Hash.ToLowerInvariant()
[ordered]@{
    schemaVersion = '1'
    artifactKind = 'facetint-provider-binding-evidence'
    status = 'PASS'
    scope = 'copied-plugin FO4 RACE/CLFM provider binding'
    runtimeClaim = $false
    gameLoadabilityClaim = $false
    plugin = [ordered]@{ name = $plugin; path = (Join-Path $data $plugin); sha256 = $pluginHash }
    resolverResponse = [ordered]@{ path = $response; sha256 = $responseHash; schemaVersion = '3' }
    expectedBinding = [ordered]@{
        npcFormId = '0x00000800'; raceFormId = '0x00000801'; colorFormId = '0x00000802'
        sex = 'female'; categoryIndex = 12; optionIndex = 42; templateIndex = 3
        colorDataKind = 'rgb'; rgb = @(64, 32, 16)
    }
    independentVerifier = $verifier
    boundary = 'All plugin, response, and report paths are K-local copies; this proves static copied-plugin binding only.'
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $report -Encoding utf8

Write-Output "FACETINT PROVIDER-BINDING FIXTURE PASS $root"
