param(
    [string]$OutputRoot = ""
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = (Resolve-Path (Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe')).Path
$cli = (Resolve-Path (Join-Path $projectRoot 'src\NpcManager.Cli\bin\Release\net10.0\npcm.dll')).Path
$output = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $projectRoot '03-builds\work\m5-sse-bodyslide-sliderpreset-evidence'
} else { [IO.Path]::GetFullPath($OutputRoot) }
$prefix = $projectRoot.TrimEnd('\') + '\'
if (-not ($output.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or
         $output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'SliderPreset evidence must remain under the project root.'
}
if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $output | Out-Null

function Write-Utf8([string]$path, [string]$value) {
    [IO.File]::WriteAllText($path, $value, (New-Object Text.UTF8Encoding($false)))
}

$valid = Join-Path $output 'Hourglass Fixture.xml'
Write-Utf8 $valid @'
<?xml version="1.0" encoding="UTF-8"?>
<SliderPresets>
  <Preset name="Hourglass Fixture" set="UBE SE 2.0 Release Body">
    <Group name="UBE"/>
    <SetSlider name="Hip_Wide" size="big" value="87.5"/>
    <SetSlider name="Hip_Wide" size="small" value="42.25"/>
  </Preset>
</SliderPresets>
'@
$validResponse = Join-Path $output 'valid-response.json'
& $dotnet $cli body sliders inspect-preset --game skyrimse --preset-xml $valid --json |
    Out-File -LiteralPath $validResponse -Encoding utf8
if ($LASTEXITCODE -ne 0) {
    throw 'Valid SliderPreset XML was refused.'
}
$result = Get-Content -Raw -LiteralPath $validResponse | ConvertFrom-Json
if (-not $result.isValid -or
    $result.schemaVersion -ne '1' -or
    $result.presetName -ne 'Hourglass Fixture' -or
    $result.sliderSet -ne 'UBE SE 2.0 Release Body' -or
    $result.groups.Count -ne 1 -or
    $result.groups[0] -ne 'UBE' -or
    $result.sliders.Count -ne 2 -or
    [double]$result.sliders[0].value -ne 87.5 -or
    [double]$result.sliders[1].value -ne 42.25) {
    throw 'SliderPreset inspection lost native percent or identity fields.'
}

$duplicate = Join-Path $output 'duplicate.xml'
Write-Utf8 $duplicate @'
<SliderPresets>
  <Preset name="Duplicate" set="UBE SE 2.0 Release Body">
    <SetSlider name="Hip_Wide" size="big" value="50"/>
    <SetSlider name="Hip_Wide" size="big" value="75"/>
  </Preset>
</SliderPresets>
'@
& $dotnet $cli body sliders inspect-preset --game skyrimse --preset-xml $duplicate --json |
    Out-File -LiteralPath (Join-Path $output 'duplicate-refused.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) {
    throw 'Duplicate name-plus-size rows did not fail closed.'
}

$malformed = Join-Path $output 'malformed.xml'
Write-Utf8 $malformed '<SliderPresets><Preset'
& $dotnet $cli body sliders inspect-preset --game skyrimse --preset-xml $malformed --json |
    Out-File -LiteralPath (Join-Path $output 'malformed-refused.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) {
    throw 'Malformed SliderPreset XML did not fail closed.'
}

$wrongExtension = Join-Path $output 'wrong-extension.txt'
Write-Utf8 $wrongExtension '<SliderPresets/>'
& $dotnet $cli body sliders inspect-preset --game skyrimse --preset-xml $wrongExtension --json |
    Out-File -LiteralPath (Join-Path $output 'wrong-extension-refused.json') -Encoding utf8
if ($LASTEXITCODE -ne 4) {
    throw 'Wrong SliderPreset extension did not fail closed.'
}

Write-Output "SSE BODYSLIDE SLIDERPRESET FIXTURES PASS $output"
