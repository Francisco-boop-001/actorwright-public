param(
    [string]$PublishRoot = "",
    [string]$ScreenshotRoot = "",
    [string]$RunTag = "20260723-1",
    [string]$ReportPath = ""
)

$ErrorActionPreference = "Stop"
$shared = Join-Path $PSScriptRoot `
    "run_sky_gui_018_packaged_acceptance.ps1"
& $shared -PublishRoot $PublishRoot -ScreenshotRoot $ScreenshotRoot `
    -RunTag $RunTag -ReportPath $ReportPath -Gate021
if (-not $?) {
    throw "The shared SKY-GUI-021 acceptance workflow failed."
}
