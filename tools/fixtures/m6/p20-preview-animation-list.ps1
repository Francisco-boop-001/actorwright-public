$ErrorActionPreference = 'Stop'
$project = 'K:\ExampleWorkspace\projects\NpcManagerReimplementation'
$workspace = 'K:\ExampleWorkspace'
$dotnet = Join-Path $workspace 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_animation_list.py'
$root = Join-Path $project '03-builds\work\m6-preview-animation-list-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$manifest = Join-Path $root 'animations.json'
$payload = [ordered]@{
    schemaVersion = 1; edition = 'fallout4'
    animations = @(
        [ordered]@{ id = 'walk'; name = 'Walk Forward'; path = 'animations/MT/Neutral/walk.hkx'; skeleton = 'meshes/skeleton.nif'; frames = 10; fps = 30; roles = @('Core', 'MT') }
        [ordered]@{ id = 'attack'; name = 'Power Attack'; path = 'animations/Weapon/attack.hkx'; skeleton = 'meshes/skeleton.nif'; frames = 12; fps = 30; additive = $true; role = 'Weapon'; femaleOnly = $true; firstPersonOnly = $true; behaviorGraph = $true }
        [ordered]@{ id = 'talk'; name = 'Talk Gesture'; path = 'animations/Dialogue/talk.hkx'; skeleton = 'meshes/skeleton.nif'; frames = 8; fps = 24; role = 'Idle'; category = 'Talk' }
        [ordered]@{ id = 'sneak'; name = 'Sneak'; path = 'animations/MT/Sneak/sneak.hkx'; skeleton = 'meshes/skeleton.nif'; frames = 9; fps = 30; role = 'MT'; firstPersonOnly = $false }
    )
}
[IO.File]::WriteAllText($manifest, ($payload | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$default = Join-Path $root 'default.json'
$filtered = Join-Path $root 'filtered.json'
& $dotnet $cli animation list --edition fallout4 --manifest $manifest --json |
    Out-File -LiteralPath $default -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Default animation-list discovery failed' }
& $dotnet $cli animation list --game fallout4 --manifest $manifest --female true --first-person true --filter 'talk gesture' --json |
    Out-File -LiteralPath $filtered -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Filtered animation-list discovery failed' }
& python $verifier --default $default --filtered $filtered --manifest $manifest | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Independent animation-list verification failed' }
Write-Output "PREVIEW ANIMATION-LIST FIXTURE PASS $root"
