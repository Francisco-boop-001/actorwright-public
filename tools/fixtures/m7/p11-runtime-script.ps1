param([string]$OutputRoot = "")

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$proposalVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_proposal.py')).Path
$buildVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_build.py')).Path
$packageVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_package.py')).Path
$deployVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_deploy.py')).Path
$binaryVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_binary.py')).Path
$vmadInspectVerifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_runtime_script_vmad_inspect.py')).Path
$sourceRoot = (Resolve-Path (Join-Path $projectRoot '02-normalized-resources\upstream-papyrus')).Path
$sourcePlugins = @{
    fallout4 = (Resolve-Path (Join-Path $projectRoot '01-source-copies\m2-fixtures\fo4\Data\M2FixtureFO4.esp')).Path
    skyrimse = (Resolve-Path (Join-Path $projectRoot '01-source-copies\m2-fixtures\sse\Data\M2FixtureSSE.esp')).Path
}
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) { Join-Path $projectRoot '03-builds\work\p11-runtime-script-evidence' } else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { throw 'Runtime-script evidence must remain under the project root.' }
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

function P([string]$Name, [string]$Type, $Value) { [PSCustomObject][ordered]@{ name = $Name; type = $Type; value = $Value } }
$fo4Properties = @(
    (P 'IsFemale' 'BoolValue' $true), (P 'SchemaVersion' 'IntValue' 7), (P 'OvlTemplate' 'StringArray' @('tattoo')),
    (P 'OvlPriority' 'IntArray' @(1)), (P 'OvlRed' 'FloatArray' @(0.1)), (P 'OvlGreen' 'FloatArray' @(0.2)),
    (P 'OvlBlue' 'FloatArray' @(0.3)), (P 'OvlAlpha' 'FloatArray' @(1.0)), (P 'OvlOffsetU' 'FloatArray' @(0.0)),
    (P 'OvlOffsetV' 'FloatArray' @(0.0)), (P 'OvlScaleU' 'FloatArray' @(1.0)), (P 'OvlScaleV' 'FloatArray' @(1.0)),
    (P 'SkinTemplate' 'StringValue' '')
)
$sseProperties = @(
    (P 'IsFemale' 'BoolValue' $true), (P 'SchemaVersion' 'IntValue' 7),
    (P 'OvlNode' 'StringArray' @('Body [Ovl0]')), (P 'OvlDiffuse' 'StringArray' @('textures/tattoo.dds')),
    (P 'OvlNormal' 'StringArray' @('textures/tattoo_n.dds')), (P 'OvlHasTint' 'BoolArray' @($false)),
    (P 'OvlTint' 'IntArray' @(0)), (P 'OvlHasAlpha' 'BoolArray' @($true)), (P 'OvlAlpha' 'FloatArray' @(1.0)),
    (P 'SkinSlot' 'IntArray' @(32)), (P 'SkinDiffuse' 'StringArray' @('')), (P 'SkinNormal' 'StringArray' @('')),
    (P 'SkinHasTint' 'BoolArray' @($false)), (P 'SkinTint' 'IntArray' @(0)), (P 'NodeName' 'StringArray' @('NPC Head [Head]')),
    (P 'NodeHasScale' 'BoolArray' @($true)), (P 'NodeScale' 'FloatArray' @(1.0)), (P 'NodeHasPos' 'BoolArray' @($false)),
    (P 'NodePosX' 'FloatArray' @(0.0)), (P 'NodePosY' 'FloatArray' @(0.0)), (P 'NodePosZ' 'FloatArray' @(0.0)),
    (P 'NodeHasRot' 'BoolArray' @($false)), (P 'NodeRotM0' 'FloatArray' @(1.0)), (P 'NodeRotM1' 'FloatArray' @(0.0)),
    (P 'NodeRotM2' 'FloatArray' @(0.0)), (P 'NodeRotM3' 'FloatArray' @(0.0)), (P 'NodeRotM4' 'FloatArray' @(1.0)),
    (P 'NodeRotM5' 'FloatArray' @(0.0)), (P 'NodeRotM6' 'FloatArray' @(0.0)), (P 'NodeRotM7' 'FloatArray' @(0.0)),
    (P 'NodeRotM8' 'FloatArray' @(1.0)), (P 'NodeScaleMode' 'IntArray' @(-1))
)
foreach ($item in @(@{ Edition = 'fallout4'; Script = 'NPCM_Manolov_ApplyFO4'; Properties = $fo4Properties }, @{ Edition = 'skyrimse'; Script = 'NPCM_Manolov_ApplySSE'; Properties = $sseProperties })) {
    $appearance = Join-Path $output "$($item.Edition)-appearance.json"
    [PSCustomObject][ordered]@{ schemaVersion = 1; scriptName = $item.Script; properties = $item.Properties; objectReferences = @(); fragments = @() } |
        ConvertTo-Json -Depth 8 | Out-File -LiteralPath $appearance -Encoding utf8
    $proposal = Join-Path $output "$($item.Edition).runtime-script-proposal.json"
    $result = & $dotnet $cli runtime-script propose --game $item.Edition --npc 0x000800 --appearance "@$appearance" --plugin $sourcePlugins[$item.Edition] --output $proposal --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script proposal failed for $($item.Edition): $result" }
    $result | Out-File -LiteralPath (Join-Path $output "$($item.Edition)-proposal-result.json") -Encoding utf8
    & $python $proposalVerifier --proposal $proposal --edition $item.Edition
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script proposal verification failed for $($item.Edition)." }
    $binary = Join-Path $output "$($item.Edition).runtime-script-vmad.esp"
    $binaryResult = & $dotnet $cli runtime-script write --game $item.Edition --source $sourcePlugins[$item.Edition] --proposal $proposal --output $binary --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script VMAD write failed for $($item.Edition): $binaryResult" }
    $binaryResult | Out-File -LiteralPath (Join-Path $output "$($item.Edition)-vmad-result.json") -Encoding utf8
    & $python $binaryVerifier --output $binary --proposal $proposal --source $sourcePlugins[$item.Edition]
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script VMAD verification failed for $($item.Edition)." }
    $inspect = Join-Path $output "$($item.Edition)-vmad-inspect.json"
    $inspectResult = & $dotnet $cli runtime-script inspect-vmad --game $item.Edition --plugin $binary --npc 0x000800 --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script VMAD inspection failed for $($item.Edition): $inspectResult" }
    $inspectResult | Out-File -LiteralPath $inspect -Encoding utf8
    & $python $vmadInspectVerifier --json $inspect --plugin $binary --proposal $proposal --edition $item.Edition
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script VMAD inspection verification failed for $($item.Edition)." }
    $preservedSource = Join-Path $output "$($item.Edition)-keep.esp"
    Copy-Item -LiteralPath $binary -Destination $preservedSource
    $preservedBytes = [IO.File]::ReadAllBytes($preservedSource)
    $oldName = [Text.Encoding]::UTF8.GetBytes($item.Script)
    $newName = [Text.Encoding]::UTF8.GetBytes(('Existing_' + $item.Script).Substring(0, $oldName.Length))
    $nameOffset = -1
    for ($index = 0; $index -le $preservedBytes.Length - $oldName.Length; $index++) {
        $match = $true
        for ($nameIndex = 0; $nameIndex -lt $oldName.Length; $nameIndex++) { if ($preservedBytes[$index + $nameIndex] -ne $oldName[$nameIndex]) { $match = $false; break } }
        if ($match) { $nameOffset = $index; break }
    }
    if ($nameOffset -lt 0) { throw "Could not locate the seed VMAD script name for $($item.Edition)." }
    [Array]::Copy($newName, 0, $preservedBytes, $nameOffset, $newName.Length)
    [IO.File]::WriteAllBytes($preservedSource, $preservedBytes)
    $preservedProposal = Join-Path $output "$($item.Edition)-preserved.runtime-script-proposal.json"
    $preservedProposalResult = & $dotnet $cli runtime-script propose --game $item.Edition --npc 0x000800 --appearance "@$appearance" --plugin $preservedSource --output $preservedProposal --json
    if ($LASTEXITCODE -ne 0) { throw "Preservation proposal failed for $($item.Edition): $preservedProposalResult" }
    $preservedOutput = Join-Path $output "$($item.Edition)-preserved.runtime-script-vmad.esp"
    $preservedWriteResult = & $dotnet $cli runtime-script write --game $item.Edition --source $preservedSource --proposal $preservedProposal --output $preservedOutput --json
    if ($LASTEXITCODE -ne 0) { throw "Preservation VMAD write failed for $($item.Edition): $preservedWriteResult" }
    & $python $binaryVerifier --output $preservedOutput --proposal $preservedProposal --source $preservedSource
    if ($LASTEXITCODE -ne 0) { throw "Independent preservation verification failed for $($item.Edition)." }
    $existingResult = & $dotnet $cli runtime-script write --game $item.Edition --source $sourcePlugins[$item.Edition] --proposal $proposal --output $binary --json 2>$null
    if ($LASTEXITCODE -ne 4) { throw "Runtime-script writer did not refuse an existing output for $($item.Edition)." }
    $tamperedSource = Join-Path $output "$($item.Edition)-tampered-source.esp"
    Copy-Item -LiteralPath $sourcePlugins[$item.Edition] -Destination $tamperedSource
    [IO.File]::AppendAllText($tamperedSource, 'tamper', (New-Object Text.UTF8Encoding($false)))
    $tamperedOutput = Join-Path $output "$($item.Edition)-tampered-output.esp"
    $tamperedResult = & $dotnet $cli runtime-script write --game $item.Edition --source $tamperedSource --proposal $proposal --output $tamperedOutput --json 2>$null
    if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $tamperedOutput)) { throw "Runtime-script writer did not refuse a mismatched source hash for $($item.Edition)." }
    $unsupportedProposal = Join-Path $output "$($item.Edition)-unsupported.runtime-script-proposal.json"
    $unsupportedText = [IO.File]::ReadAllText($proposal).Replace('"objectReferences": []', '"objectReferences": [{"name":"Unbound","reference":"Fixture.esp|0x000800"}]')
    [IO.File]::WriteAllText($unsupportedProposal, $unsupportedText, (New-Object Text.UTF8Encoding($false)))
    $unsupportedOutput = Join-Path $output "$($item.Edition)-unsupported-output.esp"
    $unsupportedResult = & $dotnet $cli runtime-script write --game $item.Edition --source $sourcePlugins[$item.Edition] --proposal $unsupportedProposal --output $unsupportedOutput --json 2>$null
    if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $unsupportedOutput)) { throw "Runtime-script writer did not refuse unbound object references for $($item.Edition)." }
    $nullPropertyProposal = Join-Path $output "$($item.Edition)-null-property.runtime-script-proposal.json"
    $nullPropertyText = [IO.File]::ReadAllText($proposal).Replace('"name": "IsFemale"', '"name": null')
    [IO.File]::WriteAllText($nullPropertyProposal, $nullPropertyText, (New-Object Text.UTF8Encoding($false)))
    $nullPropertyOutput = Join-Path $output "$($item.Edition)-null-property-output.esp"
    $nullPropertyResult = & $dotnet $cli runtime-script write --game $item.Edition --source $sourcePlugins[$item.Edition] --proposal $nullPropertyProposal --output $nullPropertyOutput --json 2>$null
    if ($LASTEXITCODE -ne 4 -or (Test-Path -LiteralPath $nullPropertyOutput)) { throw "Runtime-script writer did not refuse a null property name for $($item.Edition)." }
    $build = Join-Path $output "$($item.Edition).runtime-script-build.json"
    $buildResult = & $dotnet $cli runtime-script build --game $item.Edition --source-root $sourceRoot --output $build --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script evidence build failed for $($item.Edition): $buildResult" }
    $buildResult | Out-File -LiteralPath (Join-Path $output "$($item.Edition)-build-result.json") -Encoding utf8
    & $python $buildVerifier --evidence $build --edition $item.Edition
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script build verification failed for $($item.Edition)." }
    $package = Join-Path $output "$($item.Edition)-package"
    $packageResult = & $dotnet $cli runtime-script package --game $item.Edition --source-root $sourceRoot --output-root $package --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script package failed for $($item.Edition): $packageResult" }
    $packageResult | Out-File -LiteralPath (Join-Path $output "$($item.Edition)-package-result.json") -Encoding utf8
    & $python $packageVerifier --manifest (Join-Path $package 'runtime-script-package.json') --edition $item.Edition
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script package verification failed for $($item.Edition)." }
    $dataRoot = Join-Path $output "$($item.Edition)-copied-game\Data"
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    $deployResult = & $dotnet $cli runtime-script deploy --game $item.Edition --package (Join-Path $package 'runtime-script-package.json') --data-root $dataRoot --json
    if ($LASTEXITCODE -ne 0) { throw "Runtime-script deployment failed for $($item.Edition): $deployResult" }
    $deployResult | Out-File -LiteralPath (Join-Path $output "$($item.Edition)-deploy-result.json") -Encoding utf8
    & $python $deployVerifier --manifest (Join-Path $package 'runtime-script-package.json') --data-root $dataRoot --edition $item.Edition
    if ($LASTEXITCODE -ne 0) { throw "Independent runtime-script deployment verification failed for $($item.Edition)." }
    $idempotentResult = & $dotnet $cli runtime-script deploy --game $item.Edition --package (Join-Path $package 'runtime-script-package.json') --data-root $dataRoot --json
    if ($LASTEXITCODE -ne 0) { throw "Idempotent runtime-script deployment failed for $($item.Edition): $idempotentResult" }
}
Write-Output "P11 RUNTIME SCRIPT FIXTURES PASS $output"
