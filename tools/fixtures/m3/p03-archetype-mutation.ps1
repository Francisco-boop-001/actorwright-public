param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..\')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$generatorProject = (Resolve-Path (Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj')).Path
$generator = Join-Path $projectRoot 'tools\fixtures\m2\bin\Release\net10.0\fixture-generator.dll'
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$verifier = (Resolve-Path (Join-Path $projectRoot 'tools\verification\verify_archetype_mutation.py')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\p03-archetype-mutation-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }

$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Archetype-mutation evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$python = (Get-Command python -ErrorAction Stop).Source

if (-not (Test-Path -LiteralPath $generator)) {
    & $dotnet build $generatorProject --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generator build failed.' }
}

function Find-FullPayloadOffset([byte[]]$bytes) {
    $candidate = -1
    for ($i = 0; $i -le $bytes.Length - 10; $i++) {
        if ($bytes[$i] -eq [byte][char]'F' -and $bytes[$i + 1] -eq [byte][char]'U' -and
            $bytes[$i + 2] -eq [byte][char]'L' -and $bytes[$i + 3] -eq [byte][char]'L') {
            $size = [BitConverter]::ToUInt16($bytes, $i + 4)
            if ($size -gt 0 -and $i + 6 + $size -le $bytes.Length) { $candidate = $i + 6 }
        }
    }
    if ($candidate -ge 0) { return $candidate }
    throw 'Fixture output did not contain an NPC FULL subrecord.'
}

function Invoke-IndependentVerifier([string]$source, [string]$outputPlugin, [string]$evidence,
    [string[]]$expected, [string]$expect) {
    $arguments = @($verifier, '--source', $source, '--output', $outputPlugin, '--json', $evidence,
        '--form-id', '0x00000800', '--expect', $expect)
    foreach ($item in $expected) { $arguments += @('--expected', $item) }
    & $python @arguments
    if ($LASTEXITCODE -ne 0) { throw "Independent archetype verifier failed for $expect." }
}

$fixtures = @(
    @{ Edition = 'fallout4'; Name = 'M3ArchetypeFO4'; Source = 'M3ArchetypeFO4.esp'; ExpectedPlugin = 'M3ArchetypeFO4.esp' },
    @{ Edition = 'skyrimse'; Name = 'M3ArchetypeSSE'; Source = 'M3ArchetypeSSE.esp'; ExpectedPlugin = 'M3ArchetypeSSE.esp' }
)
$sourceFo4 = Join-Path $output 'M3ArchetypeFO4.esp'
$sourceSse = Join-Path $output 'M3ArchetypeSSE.esp'
& $dotnet $generator '--archetype' $sourceFo4 $sourceSse
if ($LASTEXITCODE -ne 0) { throw 'Dual-game archetype fixture generation failed.' }

foreach ($fixture in $fixtures) {
    $source = Join-Path $output $fixture.Source
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $outputPlugin = Join-Path $output "$($fixture.Name)-mutated.esp"
    $evidence = Join-Path $output "$($fixture.Edition)-allowed.json"
    $expected = @(
        "Race=$($fixture.ExpectedPlugin)|0x00000805",
        "Voice=$($fixture.ExpectedPlugin)|0x00000806",
        "Class=$($fixture.ExpectedPlugin)|0x00000807",
        "CombatStyle=$($fixture.ExpectedPlugin)|0x00000808"
    )
    $patchArgs = @($cli, 'npc', 'patch', '--game', $fixture.Edition, '--input-plugin', $source,
        '--output', $outputPlugin, '--form-id', '0x00000800',
        '--race', "$($fixture.ExpectedPlugin)|0x00000805",
        '--voice', "$($fixture.ExpectedPlugin)|0x00000806",
        '--class', "$($fixture.ExpectedPlugin)|0x00000807",
        '--combat-style', "$($fixture.ExpectedPlugin)|0x00000808",
        '--expected-sha256', $hash, '--apply', '--json')
    & $dotnet @patchArgs | Out-File -LiteralPath $evidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Archetype mutation failed for $($fixture.Edition)." }
    Invoke-IndependentVerifier $source $outputPlugin $evidence $expected 'allowed'

    $verifyEvidence = Join-Path $output "$($fixture.Edition)-plugin-verify.json"
    $verifyArgs = @($cli, 'plugin', 'verify', '--game', $fixture.Edition, '--source-plugin', $source,
        '--output-plugin', $outputPlugin, '--form-id', '0x00000800',
        '--race', "$($fixture.ExpectedPlugin)|0x00000805",
        '--voice', "$($fixture.ExpectedPlugin)|0x00000806",
        '--class', "$($fixture.ExpectedPlugin)|0x00000807",
        '--combat-style', "$($fixture.ExpectedPlugin)|0x00000808", '--json')
    & $dotnet @verifyArgs | Out-File -LiteralPath $verifyEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "CLI plugin verification failed for $($fixture.Edition)." }

    $cleared = Join-Path $output "$($fixture.Name)-cleared.esp"
    $clearEvidence = Join-Path $output "$($fixture.Edition)-clear.json"
    $clearArgs = @($cli, 'npc', 'patch', '--game', $fixture.Edition, '--input-plugin', $source,
        '--output', $cleared, '--form-id', '0x00000800', '--voice', 'none', '--combat-style', 'none',
        '--expected-sha256', $hash, '--apply', '--json')
    & $dotnet @clearArgs | Out-File -LiteralPath $clearEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Optional-reference clearing failed for $($fixture.Edition)." }
    Invoke-IndependentVerifier $source $cleared $clearEvidence @('Voice=none', 'CombatStyle=none') 'allowed'

    $tampered = Join-Path $output "$($fixture.Name)-tampered.esp"
    [IO.File]::Copy($outputPlugin, $tampered, $true)
    $bytes = [IO.File]::ReadAllBytes($tampered)
    $fullPayload = Find-FullPayloadOffset $bytes
    $bytes[$fullPayload] = [byte]($bytes[$fullPayload] -bxor 1)
    [IO.File]::WriteAllBytes($tampered, $bytes)
    $tamperedEvidence = Join-Path $output "$($fixture.Edition)-tampered.json"
    $tamperedJson = Get-Content -LiteralPath $evidence -Raw | ConvertFrom-Json
    $tamperedJson.outputSha256 = (Get-FileHash -LiteralPath $tampered -Algorithm SHA256).Hash.ToLowerInvariant()
    $tamperedJson | ConvertTo-Json -Depth 20 | Out-File -LiteralPath $tamperedEvidence -Encoding utf8
    Invoke-IndependentVerifier $source $tampered $tamperedEvidence $expected 'refused'

    $externalOutput = Join-Path $output "$($fixture.Name)-external-refused.esp"
    $externalEvidence = Join-Path $output "$($fixture.Edition)-external-refused.json"
    $externalArgs = @($cli, 'npc', 'patch', '--game', $fixture.Edition, '--input-plugin', $source,
        '--output', $externalOutput, '--form-id', '0x00000800', '--race', 'Other.esp|0x00000801',
        '--expected-sha256', $hash, '--apply', '--json')
    & $dotnet @externalArgs | Out-File -LiteralPath $externalEvidence -Encoding utf8
    if ($LASTEXITCODE -ne 4) { throw "Unresolved external reference was not refused for $($fixture.Edition)." }
    if (Test-Path -LiteralPath $externalOutput) { throw 'Refused external reference created an output.' }
}

Write-Output "P03 ARCHETYPE FIXTURES PASS $output"
