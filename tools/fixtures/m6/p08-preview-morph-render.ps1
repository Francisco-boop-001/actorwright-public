$ErrorActionPreference = 'Stop'
$project = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$dotnet = Join-Path $project '..\..\tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$cli = Join-Path $project 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll'
$verifier = Join-Path $project 'tools\verification\verify_preview_morph_render.py'
$assetRoot = Join-Path $project '01-source-copies\m9-facegen-assets\lumi'
$root = Join-Path $project '03-builds\work\m6-preview-morph-render-evidence'
if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Force -Path $root | Out-Null
$nif = Join-Path $assetRoot 'femalehead.nif'
$hash = (Get-FileHash -LiteralPath $nif -Algorithm SHA256).Hash.ToLowerInvariant()
$utf8 = [Text.UTF8Encoding]::new($false)
$base = [ordered]@{
    schemaVersion = 1; edition = 'skyrimse'; npcFormId = '0x902'
    assets = @([ordered]@{ category = 'face'; path = 'femalehead.nif'; provider = 'fixture'; sha256 = $hash })
}
$neutralManifest = Join-Path $root 'neutral-scene.json'
$morphManifest = Join-Path $root 'morph-scene.json'
[IO.File]::WriteAllText($neutralManifest, ($base | ConvertTo-Json -Depth 8), $utf8)
$morph = [ordered]@{
    schemaVersion = $base.schemaVersion; edition = $base.edition; npcFormId = $base.npcFormId
    assets = $base.assets; morphs = @([ordered]@{ category = 'vertex'; name = 'Aah'; value = 0.4 })
}
[IO.File]::WriteAllText($morphManifest, ($morph | ConvertTo-Json -Depth 8), $utf8)
$neutralOutput = Join-Path $root 'neutral-scene-output.json'
$neutralImage = Join-Path $root 'neutral.png'
$neutralResponse = Join-Path $root 'neutral-response.json'
$morphOutput = Join-Path $root 'morph-scene-output.json'
$morphImage = Join-Path $root 'morph.png'
$morphResponse = Join-Path $root 'morph-response.json'
$repeatOutput = Join-Path $root 'repeat-scene-output.json'
$repeatImage = Join-Path $root 'repeat.png'
$repeatResponse = Join-Path $root 'repeat-response.json'
& $dotnet $cli preview render --edition skyrimse --manifest $neutralManifest --output $neutralOutput --asset-root $assetRoot --image-output $neutralImage --width 256 --height 256 --json | Out-File -LiteralPath $neutralResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Neutral preview render failed.' }
& $dotnet $cli preview render --edition skyrimse --manifest $morphManifest --morphs vertex --output $morphOutput --asset-root $assetRoot --image-output $morphImage --width 256 --height 256 --json | Out-File -LiteralPath $morphResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Morph preview render failed.' }
& $dotnet $cli preview render --edition skyrimse --manifest $morphManifest --morphs vertex --output $repeatOutput --asset-root $assetRoot --image-output $repeatImage --width 256 --height 256 --json | Out-File -LiteralPath $repeatResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Repeated morph preview render failed.' }
& python $verifier --neutral-status $neutralOutput --neutral-image $neutralImage --morph-status $morphOutput --morph-image $morphImage --repeat-status $repeatOutput --repeat-image $repeatImage --morph-response $morphResponse
if ($LASTEXITCODE -ne 0) { throw 'Independent morph preview verifier failed.' }
$badManifest = Join-Path $root 'bad-morph-scene.json'
$bad = [ordered]@{
    schemaVersion = $base.schemaVersion; edition = $base.edition; npcFormId = $base.npcFormId
    assets = $base.assets; morphs = @([ordered]@{ category = 'vertex'; name = 'MissingMorph'; value = 0.4 })
}
[IO.File]::WriteAllText($badManifest, ($bad | ConvertTo-Json -Depth 8), $utf8)
$badOutput = Join-Path $root 'bad-morph-output.json'
$badImage = Join-Path $root 'bad-morph.png'
& $dotnet $cli preview render --edition skyrimse --manifest $badManifest --morphs vertex --output $badOutput --asset-root $assetRoot --image-output $badImage --width 256 --height 256 --json | Out-Null
if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $badImage)) { throw 'Missing TRI morph was accepted.' }
Write-Output "PREVIEW MORPH RENDER FIXTURE PASS $root"
