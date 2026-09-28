$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_plugin_surface_audit.py'
$source = Join-Path $project '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp'
$fo4Source = Join-Path $project '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp'
$root = Join-Path $project '03-builds\work\m9-plugin-surface-audit'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$unchangedResponse = Join-Path $root 'unchanged.json'
$changed = Join-Path $root 'changed.esp'
$proposal = Join-Path $root 'changed.npc-proposal.json'
$changedResponse = Join-Path $root 'changed.json'
$unsafeResponse = Join-Path $root 'unsafe.json'

& $dotnet $cli plugin audit --game skyrimse --before $source --after $source --json |
    Out-File -LiteralPath $unchangedResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Unchanged plugin audit failed' }
& python $verifier --json $unchangedResponse --before $source --after $source --edition skyrimse --mode unchanged
if ($LASTEXITCODE -ne 0) { throw 'Independent unchanged audit verification failed' }

& $dotnet $cli npc patch --game skyrimse --input-plugin $source --output $changed --form-id 0x00000800 `
    --sex female --expected-sha256 ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()) `
    --proposal $proposal --apply --json | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Fixture NPC patch failed' }
& $dotnet $cli plugin audit --game skyrimse --before $source --after $changed --json |
    Out-File -LiteralPath $changedResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Changed plugin audit failed' }
& python $verifier --json $changedResponse --before $source --after $changed --edition skyrimse --mode changed
if ($LASTEXITCODE -ne 0) { throw 'Independent changed audit verification failed' }

$fo4Response = Join-Path $root 'fo4-unchanged.json'
& $dotnet $cli plugin audit --game fallout4 --before $fo4Source --after $fo4Source --json |
    Out-File -LiteralPath $fo4Response -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Fallout 4 plugin audit failed' }
& python $verifier --json $fo4Response --before $fo4Source --after $fo4Source --edition fallout4 --mode unchanged
if ($LASTEXITCODE -ne 0) { throw 'Independent Fallout 4 audit verification failed' }

$providerRoot = Join-Path $root 'providers-sse'
New-Item -ItemType Directory -Force -Path $providerRoot | Out-Null
$providerBase = Join-Path $providerRoot 'Base.esp'
$providerOverride = Join-Path $providerRoot 'Override.esp'
$providerManifest = Join-Path $providerRoot 'load-order.json'
Copy-Item -LiteralPath $source -Destination $providerBase
Copy-Item -LiteralPath $source -Destination $providerOverride
@'
{
  "schemaVersion": 1,
  "edition": "skyrimse",
  "plugins": [
    { "name": "Base.esp", "order": 0, "enabled": true },
    { "name": "Override.esp", "order": 1, "enabled": true }
  ]
}
'@ | ForEach-Object { [System.IO.File]::WriteAllText($providerManifest, $_, [System.Text.UTF8Encoding]::new($false)) }
$providerResponse = Join-Path $root 'providers-sse.json'
& $dotnet $cli plugin audit --game skyrimse --before $providerBase --after $providerOverride `
    --plugins-root $providerRoot --load-order $providerManifest --json |
    Out-File -LiteralPath $providerResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Skyrim provider-resolution audit failed' }
& python $verifier --json $providerResponse --before $providerBase --after $providerOverride --edition skyrimse `
    --mode providers --expected-winner Override.esp --expected-chain Base.esp Override.esp
if ($LASTEXITCODE -ne 0) { throw 'Independent Skyrim provider-resolution verification failed' }

$providerRootFo4 = Join-Path $root 'providers-fo4'
New-Item -ItemType Directory -Force -Path $providerRootFo4 | Out-Null
$providerBaseFo4 = Join-Path $providerRootFo4 'Base.esp'
$providerOverrideFo4 = Join-Path $providerRootFo4 'Override.esp'
$providerManifestFo4 = Join-Path $providerRootFo4 'load-order.json'
Copy-Item -LiteralPath $fo4Source -Destination $providerBaseFo4
Copy-Item -LiteralPath $fo4Source -Destination $providerOverrideFo4
@'
{
  "schemaVersion": 1,
  "edition": "fallout4",
  "plugins": [
    { "name": "Base.esp", "order": 0, "enabled": true },
    { "name": "Override.esp", "order": 1, "enabled": true }
  ]
}
'@ | ForEach-Object { [System.IO.File]::WriteAllText($providerManifestFo4, $_, [System.Text.UTF8Encoding]::new($false)) }
$providerResponseFo4 = Join-Path $root 'providers-fo4.json'
& $dotnet $cli plugin audit --game fallout4 --before $providerBaseFo4 --after $providerOverrideFo4 `
    --data-root $providerRootFo4 --loadorder $providerManifestFo4 --json |
    Out-File -LiteralPath $providerResponseFo4 -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Fallout 4 provider-resolution audit failed' }
& python $verifier --json $providerResponseFo4 --before $providerBaseFo4 --after $providerOverrideFo4 --edition fallout4 `
    --mode providers --expected-winner Override.esp --expected-chain Base.esp Override.esp
if ($LASTEXITCODE -ne 0) { throw 'Independent Fallout 4 provider-resolution verification failed' }

$ErrorActionPreference = 'Continue'
& $dotnet $cli plugin audit --game skyrimse --before 'F:\ExampleGame\Data\M2FixtureSSE.esp' --after $source --json |
    Out-File -LiteralPath $unsafeResponse -Encoding utf8
$unsafeExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($unsafeExit -ne 3) { throw 'Unsafe plugin audit input was not refused' }
& python $verifier --json $unsafeResponse --before $source --after $source --edition skyrimse --mode unsafe
if ($LASTEXITCODE -ne 0) { throw 'Independent unsafe audit verification failed' }

Write-Output "PLUGIN SURFACE AUDIT FIXTURE PASS $root (read-only record-family audit; no deployment or runtime claim)"
