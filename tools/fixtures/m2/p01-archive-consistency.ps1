param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\p01-archive-consistency-evidence'
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}

$projectPrefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Archive-consistency evidence must remain under the project root.'
}
New-Item -ItemType Directory -Force -Path $output | Out-Null
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:PATH = "$(Split-Path -Parent $dotnet);$env:PATH"

$fixtures = @(
    @{ Edition = 'fallout4'; Game = 'fo4'; Plugin = 'M2FixtureFO4.esp'; Archive = 'M2Fixture - Main.ba2' },
    @{ Edition = 'skyrimse'; Game = 'sse'; Plugin = 'M2FixtureSSE.esp'; Archive = 'M2Fixture.bsa' }
)

foreach ($fixture in $fixtures) {
    $data = (Resolve-Path (Join-Path $projectRoot "01-source-copies\m2-fixtures\$($fixture.Game)\Data")).Path
    $plugin = (Resolve-Path (Join-Path $data $fixture.Plugin)).Path
    $index = Join-Path $output "$($fixture.Game)-index.json"
    $allowed = Join-Path $output "$($fixture.Game)-allowed.json"
    foreach ($path in @($index, $allowed)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    & $dotnet $cli assets index --edition $fixture.Edition --data-root $data --output $index --json | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Asset-index export failed for $($fixture.Edition)." }
    & $dotnet $cli workspace preflight --game $fixture.Edition --plugin $plugin --asset-index $index --json |
        Out-File -LiteralPath $allowed -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw "Archive-consistency preflight failed for $($fixture.Edition)." }
}

$fo4Index = Join-Path $output 'fo4-index.json'
$mismatchIndex = Join-Path $output 'fo4-mismatch-index.json'
$mismatch = Join-Path $output 'fo4-mismatch.json'
if (Test-Path -LiteralPath $mismatchIndex) { Remove-Item -LiteralPath $mismatchIndex -Force }
if (Test-Path -LiteralPath $mismatch) { Remove-Item -LiteralPath $mismatch -Force }
$tampered = (Get-Content -LiteralPath $fo4Index -Raw).Replace('M2Fixture - Main.ba2', 'M2Fixture - Missing.ba2')
[IO.File]::WriteAllText($mismatchIndex, $tampered, (New-Object Text.UTF8Encoding($false)))
$fo4Data = (Resolve-Path (Join-Path $projectRoot '01-source-copies\m2-fixtures\fo4\Data')).Path
$fo4Plugin = (Resolve-Path (Join-Path $fo4Data 'M2FixtureFO4.esp')).Path
& $dotnet $cli workspace preflight --game fallout4 --plugin $fo4Plugin --asset-index $mismatchIndex --json |
    Out-File -LiteralPath $mismatch -Encoding utf8
if ($LASTEXITCODE -ne 4) { throw "Expected validation exit 4 for the tampered archive index, got $LASTEXITCODE." }

Write-Output "ARCHIVE CONSISTENCY FIXTURES PASS $output"
