param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$DotNetPath,
    [string]$RestoreConfigFile,
    [switch]$RequireCleanTree,
    [switch]$NoHttpCache,
    [ValidateRange(1, 3600)]
    [int]$SelectorTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$systemCommitTypeDefinition = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class ActorwrightBuildSystemCommit
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        // CommitPeak is present for native layout only; observed peak comes
        // from sampled CommitTotal values.
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal,
            PhysicalAvailable, SystemCache, KernelTotal, KernelPaged,
            KernelNonpaged, PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(
        ref PerformanceInformation information,
        uint size);

    public static ulong[] ReadBytes()
    {
        var information = new PerformanceInformation {
            Size = (uint)Marshal.SizeOf(typeof(PerformanceInformation))
        };
        if (!GetPerformanceInfo(ref information, information.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong pageBytes = information.PageSize.ToUInt64();
        return new[] {
            checked(information.CommitTotal.ToUInt64() * pageBytes),
            checked(information.CommitLimit.ToUInt64() * pageBytes)
        };
    }
}
'@

function Get-ActorwrightSystemCommitSample {
    $values = [ActorwrightBuildSystemCommit]::ReadBytes()
    [pscustomobject]@{
        TimeUtc = [DateTimeOffset]::UtcNow.ToString('o')
        CommitBytes = [UInt64]$values[0]
        CommitLimitBytes = [UInt64]$values[1]
    }
}

function Set-CanonicalNuGetHttpCache([string]$Artifacts) {
    $env:NUGET_HTTP_CACHE_PATH = [IO.Path]::GetFullPath(
        (Join-Path $Artifacts 'nuget-http-cache'))
    New-Item -ItemType Directory -Force -Path `
        $env:NUGET_HTTP_CACHE_PATH | Out-Null
}

$systemCommitSampleIntervalMilliseconds = 1000
$systemCommitJob = $null
$systemCommitStart = $null
$systemCommitSamples = @()
$systemCommitJobErrors = @()
$systemCommitSamplingFailure = $null
$canonicalBuildFailure = $null

try {
if ($null -eq ('ActorwrightBuildSystemCommit' -as [type])) {
    Add-Type -TypeDefinition $systemCommitTypeDefinition
}
$systemCommitStart = Get-ActorwrightSystemCommitSample
$systemCommitJob = Start-Job -ArgumentList `
    $systemCommitTypeDefinition, $systemCommitSampleIntervalMilliseconds `
    -ScriptBlock {
        param([string]$TypeDefinition, [int]$SampleIntervalMilliseconds)
        $ErrorActionPreference = 'Stop'
        Add-Type -TypeDefinition $TypeDefinition
        while ($true) {
            $values = [ActorwrightBuildSystemCommit]::ReadBytes()
            [pscustomobject]@{
                TimeUtc = [DateTimeOffset]::UtcNow.ToString('o')
                CommitBytes = [UInt64]$values[0]
                CommitLimitBytes = [UInt64]$values[1]
            }
            Start-Sleep -Milliseconds $SampleIntervalMilliseconds
        }
    }

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $projectRoot 'Actorwright.sln'
$git = (Get-Command git.exe -ErrorAction Stop).Source
$sourceCommit = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Unable to resolve canonical-build source commit'
}
$sourceTree = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse 'HEAD^{tree}').Trim()
if ($LASTEXITCODE -ne 0 -or $sourceTree -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Unable to resolve canonical-build source tree'
}
$workingTreeStatus = @(& $git -c core.fsmonitor=false -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect canonical-build worktree' }
$worktreeState = if ($workingTreeStatus.Count -eq 0) { 'CLEAN' } else { 'DIRTY' }
if ($RequireCleanTree -and $worktreeState -ne 'CLEAN') {
    throw 'Canonical build requires a clean worktree'
}
$restoreConfig = if ($RestoreConfigFile) {
    (Resolve-Path -LiteralPath $RestoreConfigFile -ErrorAction Stop).Path
} else {
    (Resolve-Path -LiteralPath (Join-Path $projectRoot 'nuget.config') -ErrorAction Stop).Path
}
$restoreConfigMode = if ($RestoreConfigFile) { 'explicit-override' } else { 'tracked-default' }
$trackedRestoreConfig = (Resolve-Path -LiteralPath (Join-Path $projectRoot 'nuget.config') -ErrorAction Stop).Path
$restoreConfigHash = (Get-FileHash -LiteralPath $restoreConfig -Algorithm SHA256).Hash
$trackedRestoreConfigHash = (Get-FileHash -LiteralPath $trackedRestoreConfig -Algorithm SHA256).Hash
$nugetAuditSetting = [Environment]::GetEnvironmentVariable('NuGetAudit', 'Process')
if ([string]::IsNullOrWhiteSpace($nugetAuditSetting)) { $nugetAuditSetting = 'default-enabled' }

$globalJson = Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw |
    ConvertFrom-Json
$pinnedSdkVersion = [string]$globalJson.sdk.version
if ($pinnedSdkVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw 'global.json does not contain one valid pinned SDK version'
}
if ($DotNetPath) {
    $dotnet = (Resolve-Path -LiteralPath $DotNetPath).Path
} else {
    $sdkRelativePath = "artifacts\tools\dotnet-sdk-$pinnedSdkVersion\dotnet.exe"
    $gitCommonDirectory = (& $git -c core.fsmonitor=false -C $projectRoot `
        rev-parse --path-format=absolute --git-common-dir).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitCommonDirectory)) {
        throw 'Unable to resolve the shared Git directory for pinned SDK discovery'
    }
    $sharedCheckoutRoot = Split-Path -Parent $gitCommonDirectory
    $dotnet = @(
        (Join-Path $projectRoot $sdkRelativePath),
        (Join-Path $sharedCheckoutRoot $sdkRelativePath)
    ) | Select-Object -Unique | Where-Object {
        Test-Path -LiteralPath $_ -PathType Leaf
    } | Select-Object -First 1
    if (-not $dotnet) {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    }
    $dotnet = (Resolve-Path -LiteralPath $dotnet).Path
}
$selectedSdkVersion = (& $dotnet --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $selectedSdkVersion -ne $pinnedSdkVersion) {
    throw "Pinned .NET SDK version mismatch: expected $pinnedSdkVersion at $dotnet; observed '$selectedSdkVersion'"
}

Write-Output "EVIDENCE_SOURCE_COMMIT=$($sourceCommit.ToUpperInvariant())"
Write-Output "EVIDENCE_SOURCE_TREE=$($sourceTree.ToUpperInvariant())"
Write-Output "EVIDENCE_WORKTREE_STATUS=$worktreeState"
Write-Output "EVIDENCE_DOTNET_PATH=$dotnet"
Write-Output "EVIDENCE_DOTNET_SDK_VERSION=$selectedSdkVersion"
Write-Output "EVIDENCE_RESTORE_CONFIG=$restoreConfig"
Write-Output "EVIDENCE_RESTORE_CONFIG_MODE=$restoreConfigMode"
Write-Output "EVIDENCE_RESTORE_CONFIG_SHA256=$restoreConfigHash"
Write-Output "EVIDENCE_TRACKED_NUGET_CONFIG_SHA256=$trackedRestoreConfigHash"
Write-Output "EVIDENCE_NUGET_AUDIT=$nugetAuditSetting"
Write-Output "EVIDENCE_NUGET_HTTP_CACHE=$(if ($NoHttpCache) { 'BYPASS' } else { 'DEFAULT' })"

$env:ACTORWRIGHT_TEST_DOTNET = $dotnet

$artifacts = Join-Path $projectRoot 'artifacts'
Set-CanonicalNuGetHttpCache $artifacts
$env:DOTNET_CLI_HOME = Join-Path $artifacts 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $artifacts 'nuget-packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

$python = (Get-Command python -ErrorAction Stop).Source
$sourceValidator = Join-Path $projectRoot 'tools\architecture\validate_source_cleanliness.py'
& $python $sourceValidator --repository-root $projectRoot --dotnet $dotnet
if ($LASTEXITCODE -ne 0) {
    throw "source cleanliness validation failed (exit $LASTEXITCODE)"
}

$restoreArguments = @('restore', $solution, '--locked-mode', '--configfile', $restoreConfig)
if ($NoHttpCache) { $restoreArguments += '--no-http-cache' }
& $dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
& $dotnet build $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$registryValidator = Join-Path $projectRoot `
    'tools\architecture\validate_standalone_test_registry.py'
& $python $registryValidator
if ($LASTEXITCODE -ne 0) {
    throw "standalone test registry validation failed (exit $LASTEXITCODE)"
}

$matrixPath = Join-Path $projectRoot 'tests\standalone-test-matrix.json'
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
if ($matrix.schemaVersion -ne 2 -or $matrix.tests.Count -lt 1) {
    throw 'standalone test matrix is malformed or empty'
}
$runnableTests = @($matrix.tests | Where-Object classification -eq 'runnable')
$fixtureBoundCount = @(
    $matrix.tests | Where-Object classification -eq 'fixture-bound').Count
$unverifiedTests = @(
    $matrix.tests | Where-Object classification -eq 'unverified')
if ($unverifiedTests.Count -gt 0) {
    $unverifiedRoutes = @($unverifiedTests | ForEach-Object {
        "$($_.project) -- $($_.arguments -join ' ')"
    })
    throw (
        "unverified standalone selectors block canonical build " +
        "($($unverifiedTests.Count)): $($unverifiedRoutes -join '; ')")
}
Write-Output (
    "Standalone selector registry: runnable=$($runnableTests.Count) " +
    "fixture-bound=$fixtureBoundCount unverified=$($unverifiedTests.Count)")
$selectorLauncher = Join-Path $projectRoot `
    'tools\build\run_standalone_selectors.py'
& $python $selectorLauncher `
    --dotnet $dotnet `
    --project-root $projectRoot `
    --matrix $matrixPath `
    --configuration $Configuration `
    --timeout-seconds $SelectorTimeoutSeconds
if ($LASTEXITCODE -ne 0) {
    throw "standalone selector launcher failed (exit $LASTEXITCODE)"
}
function Assert-SourceFirewallReport([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item -or
        -not ($item -is [IO.FileInfo]) -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -le 0) {
        throw "Source compatibility firewall report is missing, empty, or not an ordinary file: $Path"
    }
}

if ($Configuration -eq 'Release') {
    $sourceCli = Join-Path $projectRoot `
        'src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe'
    $firewallScript = Join-Path $projectRoot `
        'tools\verification\compatibility-firewall.ps1'
    $firewallReportRoot = Join-Path $projectRoot 'artifacts\test-work'
    New-Item -ItemType Directory -Force -Path $firewallReportRoot | Out-Null
    $firewallReport = Join-Path $firewallReportRoot (
        "compatibility-firewall-source-" + [Guid]::NewGuid().ToString('N') + '.json')
    & $firewallScript verify `
        -Tier Source `
        -Baseline publicSynthetic `
        -SourceCli $sourceCli `
        -Output $firewallReport
    if ($LASTEXITCODE -ne 0) {
        throw "Source compatibility firewall failed (exit $LASTEXITCODE)"
    }
    Assert-SourceFirewallReport -Path $firewallReport
}

foreach ($validator in @(
    'tools\architecture\validate_architecture.py',
    'tools\architecture\validate_desktop_shell.py',
    'tools\architecture\validate_repository_independence.py'
)) {
    & $python (Join-Path $projectRoot $validator)
    if ($LASTEXITCODE -ne 0) { throw "validator failed: $validator (exit $LASTEXITCODE)" }
}

$pytestBase = Join-Path $artifacts ("pytest-" + [Guid]::NewGuid().ToString('N'))
$pytestGitConfig = @{}
foreach ($name in @('GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_0', 'GIT_CONFIG_VALUE_0')) {
    $pytestGitConfig[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:GIT_CONFIG_COUNT = '1'
    $env:GIT_CONFIG_KEY_0 = 'safe.directory'
    $env:GIT_CONFIG_VALUE_0 = '*'
    & $python -m pytest `
        (Join-Path $projectRoot 'tests\test_permission_profile.py') `
        (Join-Path $projectRoot 'tests\independence') `
        (Join-Path $projectRoot 'tests\exchange') `
        (Join-Path $projectRoot 'tests\release') `
        (Join-Path $projectRoot 'tests\architecture\test_validate_architecture.py') `
        (Join-Path $projectRoot 'tests\architecture\test_standalone_test_registry.py') `
        -q -rs --basetemp $pytestBase
    $pytestExitCode = $LASTEXITCODE
} catch {
    throw "Canonical pytest invocation failed; temporary output is preserved at $pytestBase. $($_.Exception.Message)"
} finally {
    foreach ($name in $pytestGitConfig.Keys) {
        [Environment]::SetEnvironmentVariable($name, $pytestGitConfig[$name], 'Process')
    }
}
if ($pytestExitCode -ne 0) {
    throw "permission-profile, repository-independence, exchange, release, or architecture fixture tests failed (exit $pytestExitCode); temporary output is preserved at $pytestBase"
}

if (Test-Path -LiteralPath $pytestBase) {
    $pytestCleanupCommand = @'
import re
import sys
from pathlib import Path

sys.path.insert(0, sys.argv[1])
from tools.storage_retention import assert_inside, is_reparse, remove_exact_path

project = Path(sys.argv[1])
candidate = Path(sys.argv[2])
artifacts = Path(sys.argv[3])
if not project.is_dir() or not artifacts.is_dir() or not candidate.is_dir():
    raise SystemExit('cleanup roots must be existing directories')
if is_reparse(artifacts) or is_reparse(candidate):
    raise SystemExit('reparse-point cleanup refused')
target = assert_inside(candidate, project)
resolved_artifacts = assert_inside(artifacts, project)
if (
    target.parent != resolved_artifacts
    or target.name != candidate.name
    or not re.fullmatch(r'pytest-[0-9a-f]{32}', target.name)
):
    raise SystemExit('resolved pytest path escaped its exact run-owned artifacts child')
remove_exact_path(target)
'@
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $pytestCleanupOutput = @(& $python -c $pytestCleanupCommand `
            $projectRoot $pytestBase $artifacts 2>&1 | ForEach-Object { $_.ToString() })
        $pytestCleanupExitCode = $LASTEXITCODE
    } catch {
        throw "Canonical pytest cleanup failed at $pytestBase. $($_.Exception.Message)"
    } finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($pytestCleanupExitCode -ne 0 -or (Test-Path -LiteralPath $pytestBase)) {
        throw "Canonical pytest cleanup failed at $pytestBase (exit $pytestCleanupExitCode): $($pytestCleanupOutput -join [Environment]::NewLine)"
    }
}

Write-Output "Actorwright standalone private build: PASS ($Configuration)"
Write-Output "Non-runnable selector routes: fixture-bound=$fixtureBoundCount unverified=$($unverifiedTests.Count) (see tests\standalone-test-matrix.json)"
$finalSourceCommit = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $finalSourceCommit -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Unable to resolve final canonical-build source commit'
}
$finalSourceTree = (& $git -c core.fsmonitor=false -C $projectRoot rev-parse 'HEAD^{tree}').Trim()
if ($LASTEXITCODE -ne 0 -or $finalSourceTree -notmatch '^[A-Fa-f0-9]{40}$') {
    throw 'Unable to resolve final canonical-build source tree'
}
$finalWorkingTreeStatus = @(& $git -c core.fsmonitor=false -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect final canonical-build worktree' }
$finalRestoreConfigHash = (Get-FileHash -LiteralPath $restoreConfig -Algorithm SHA256).Hash
if ($finalRestoreConfigHash -ne $restoreConfigHash) {
    throw 'Canonical-build restore configuration changed during execution'
}
$finalWorktreeState = if ($finalWorkingTreeStatus.Count -eq 0) { 'CLEAN' } else { 'DIRTY' }
if ($finalSourceCommit -ne $sourceCommit -or $finalSourceTree -ne $sourceTree) {
    throw 'Canonical-build source identity changed during execution'
}
if ($RequireCleanTree -and $finalWorktreeState -ne 'CLEAN') {
    throw 'Canonical build left a dirty worktree'
}
Write-Output "EVIDENCE_FINAL_SOURCE_COMMIT=$($finalSourceCommit.ToUpperInvariant())"
Write-Output "EVIDENCE_FINAL_SOURCE_TREE=$($finalSourceTree.ToUpperInvariant())"
Write-Output "EVIDENCE_FINAL_WORKTREE_STATUS=$finalWorktreeState"
Write-Output "EVIDENCE_FINAL_RESTORE_CONFIG_SHA256=$finalRestoreConfigHash"
} catch {
    if (-not $systemCommitStart -or -not $systemCommitJob) {
        $systemCommitSamplingFailure = $_.Exception.Message
    } else {
        $canonicalBuildFailure = $_
    }
} finally {
    try {
        if ($systemCommitJob) {
            if ($systemCommitJob.State -in @('NotStarted', 'Running')) {
                Stop-Job -Job $systemCommitJob -ErrorAction Stop
            }
            $systemCommitSamples = @(
                Receive-Job -Job $systemCommitJob `
                    -ErrorAction SilentlyContinue)
            if ($systemCommitJob.State -eq 'Failed') {
                throw 'system commit sampler job failed'
            }
            if ($systemCommitSamples.Count -eq 0 -and
                (([DateTimeOffset]::UtcNow -
                    [DateTimeOffset]$systemCommitStart.TimeUtc).TotalSeconds -gt 5)) {
                throw 'system commit sampler produced no periodic samples'
            }
        }
    } catch {
        $systemCommitSamplingFailure = $_.Exception.Message
    } finally {
        if ($systemCommitJob) {
            Remove-Job -Job $systemCommitJob -ErrorAction SilentlyContinue
        }
    }

    try {
        $systemCommitEnd = Get-ActorwrightSystemCommitSample
    } catch {
        if (-not $systemCommitSamplingFailure) {
            $systemCommitSamplingFailure = $_.Exception.Message
        }
    }

    if ($systemCommitStart -and $systemCommitEnd) {
        $allSystemCommitSamples = @(
            $systemCommitStart
            $systemCommitSamples
            $systemCommitEnd)
        $observedPeak = $allSystemCommitSamples |
            Sort-Object -Property CommitBytes -Descending |
            Select-Object -First 1
        $sampleDuration = ([DateTimeOffset]$systemCommitEnd.TimeUtc -
            [DateTimeOffset]$systemCommitStart.TimeUtc).TotalSeconds
        Write-Output "EVIDENCE_SYSTEM_COMMIT_SAMPLING=$(if ($systemCommitSamplingFailure) { 'FAIL' } else { 'PASS' })"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_SAMPLE_INTERVAL_MILLISECONDS=$systemCommitSampleIntervalMilliseconds"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_SAMPLE_COUNT=$($allSystemCommitSamples.Count)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_START_UTC=$($systemCommitStart.TimeUtc)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_START_BYTES=$($systemCommitStart.CommitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_START_LIMIT_BYTES=$($systemCommitStart.CommitLimitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_PEAK_UTC=$($observedPeak.TimeUtc)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_PEAK_BYTES=$($observedPeak.CommitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_PEAK_LIMIT_BYTES=$($observedPeak.CommitLimitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_END_UTC=$($systemCommitEnd.TimeUtc)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_END_BYTES=$($systemCommitEnd.CommitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_END_LIMIT_BYTES=$($systemCommitEnd.CommitLimitBytes)"
        Write-Output "EVIDENCE_SYSTEM_COMMIT_INTERVAL_SECONDS=$($sampleDuration.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))"
        Write-Output 'EVIDENCE_SYSTEM_COMMIT_PEAK_SCOPE=sampled interval; native lifetime CommitPeak is unused'
    } else {
        Write-Output 'EVIDENCE_SYSTEM_COMMIT_SAMPLING=FAIL'
    }
    if ($systemCommitSamplingFailure) {
        Write-Output "EVIDENCE_SYSTEM_COMMIT_SAMPLING_ERROR=$systemCommitSamplingFailure"
    }
}

if ($canonicalBuildFailure) { throw $canonicalBuildFailure }
if ($systemCommitSamplingFailure) {
    throw "System commit sampling failed: $systemCommitSamplingFailure"
}
