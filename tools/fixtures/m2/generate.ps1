$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $projectRoot '..\..')).Path
$dotnet = Join-Path $workspaceRoot 'tools\external\dotnet-sdk-10.0.301-win-x64\dotnet.exe'
$feed = Join-Path $workspaceRoot 'tools\external\nuget-feed'
$cache = Join-Path $workspaceRoot 'tools\external\nuget-packages-m2'
$generator = Join-Path $projectRoot 'tools\fixtures\m2\fixture-generator.csproj'
$archiveGenerator = Join-Path $projectRoot 'tools\fixtures\m2\archive-generator\archive-generator.csproj'
$out = Join-Path $projectRoot '01-source-copies\m2-fixtures'
if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Pinned local SDK is missing.' }
if (-not ((Resolve-Path $out).Path.StartsWith($projectRoot, [StringComparison]::OrdinalIgnoreCase))) { throw 'Fixture output escaped project root.' }
$env:DOTNET_ROOT = Split-Path $dotnet
$env:PATH = "$(Split-Path $dotnet);$env:PATH"
$env:NUGET_PACKAGES = $cache
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $dotnet restore $generator --source $feed --packages $cache --force-evaluate --no-cache
& $dotnet run --project $generator --no-restore -- $out\sse\Data\M2FixtureSSE.esp $out\fo4\Data\M2FixtureFO4.esp
if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed with exit code $LASTEXITCODE." }
& $dotnet restore $archiveGenerator --source $feed --packages $cache --locked-mode
if ($LASTEXITCODE -ne 0) { throw "Archive fixture generator restore failed with exit code $LASTEXITCODE." }
& $dotnet build $archiveGenerator --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw "Archive fixture generator build failed with exit code $LASTEXITCODE." }
$archiveDll = Join-Path $projectRoot 'tools\fixtures\m2\archive-generator\bin\Release\net10.0-windows\archive-generator.dll'
& $dotnet $archiveDll $out
if ($LASTEXITCODE -ne 0) { throw "Archive fixture generator failed with exit code $LASTEXITCODE." }
