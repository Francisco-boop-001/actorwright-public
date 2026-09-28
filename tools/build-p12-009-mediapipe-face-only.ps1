[CmdletBinding()]
param(
    [string]$WorkspaceRoot = 'K:\ExampleWorkspace'
)

$ErrorActionPreference = 'Stop'

$sourceRoot = Join-Path $WorkspaceRoot 'tools\external\mediapipe-0.10.35-win-x64\source\mediapipe-f8ef212d5c962c0e853db7e59d217056b187084b'
$bazel = Join-Path $WorkspaceRoot 'tools\external\bazel-7.4.1-win-x64\bazel.exe'
$bazelUserRoot = Join-Path $WorkspaceRoot 'tools\external\bazel-7.4.1-win-x64\user-root'
$msysBin = Join-Path $WorkspaceRoot 'tools\external\msys2-base-2026-06-11\msys64\usr\bin'
$distdir = Join-Path $WorkspaceRoot 'tools\external\mediapipe-0.10.35-win-x64\distdir'
$opencvRoot = Join-Path $WorkspaceRoot 'tools\external\opencv-3.4.10-win-x64\opencv\build'
$glogRoot = Join-Path $WorkspaceRoot 'tools\external\glog-3a0d4d22c5ae0b9a2216988411cfa6bf860cc372-npcmanager\source\glog-3a0d4d22c5ae0b9a2216988411cfa6bf860cc372'
$glogBzl = Join-Path $glogRoot 'bazel\glog.bzl'
$expectedGlogBzlSha256 = 'AFD705D53754B128E6777C7B33515F4F79F0A149FB4D41C01B54DB7EB97E8CB0'
if (-not (Test-Path -LiteralPath $glogBzl -PathType Leaf) -or
    (Get-FileHash -LiteralPath $glogBzl -Algorithm SHA256).Hash -ne $expectedGlogBzlSha256) {
    throw 'Glog override must include the reviewed static-export patch.'
}
$evidenceRoot = Join-Path $WorkspaceRoot 'projects\NpcManagerReimplementation\03-builds\work'
$stdout = Join-Path $evidenceRoot 'p12-009-mediapipe-face-only-build.stdout.log'
$stderr = Join-Path $evidenceRoot 'p12-009-mediapipe-face-only-build.stderr.log'
$buildEvent = Join-Path $evidenceRoot 'p12-009-mediapipe-face-only-build-event.json'

$env:BAZEL_SH = Join-Path $msysBin 'bash.exe'
$env:BAZEL_VC = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC'
$env:BAZEL_VC_FULL_VERSION = '14.41.34120'
$env:BAZEL_WINSDK_FULL_VERSION = '10.0.22621.0'
$env:PATH = "$msysBin;$env:PATH"

$startupArguments = @(
    "--output_user_root=$bazelUserRoot"
    '--output_base=C:\tmp\p12b7\o'
)

$buildArguments = @(
    '--repository_cache=C:\tmp\npcm-p12-009-bazel-20260725\repository-cache'
    "--distdir=$distdir"
    '--symlink_prefix=C:\tmp\p12b7\convenience\bazel-'
    "--override_repository=windows_opencv=$opencvRoot"
    "--override_repository=com_github_glog_glog_windows=$glogRoot"
    '--repo_env=HERMETIC_PYTHON_VERSION=3.12'
    "--repo_env=BAZEL_SH=$env:BAZEL_SH"
    "--repo_env=BAZEL_VC=$env:BAZEL_VC"
    "--repo_env=BAZEL_VC_FULL_VERSION=$env:BAZEL_VC_FULL_VERSION"
    "--repo_env=BAZEL_WINSDK_FULL_VERSION=$env:BAZEL_WINSDK_FULL_VERSION"
    "--repo_env=PATH=$env:PATH"
    '--action_env=PATH'
    '--noshow_progress'
    '--color=no'
    '--curses=no'
    '--verbose_failures'
    "--build_event_json_file=$buildEvent"
    '--copt=/DMP_EXPORT='
    '--copt=/DTFL_STATIC_LIBRARY_BUILD'
    '--per_file_copt=external/pthreadpool/.*\.c@/std:c11,/experimental:c11atomics'
    '--per_file_copt=mediapipe/.*\.cc@/Zc:preprocessor'
    '--per_file_copt=mediapipe/.*\.cpp@/Zc:preprocessor'
    '-c'
    'opt'
    '--strip=always'
    '--define'
    'MEDIAPIPE_DISABLE_GPU=1'
    '//mediapipe/tasks/c:npc_manager_reference_source'
)

Push-Location $sourceRoot
try {
    # Windows PowerShell surfaces native stderr as NativeCommandError when
    # ErrorActionPreference is Stop. Bazel writes ordinary warnings/progress
    # to stderr, so let the process finish and use its actual exit code.
    $ErrorActionPreference = 'Continue'
    & $bazel @startupArguments build @buildArguments 1> $stdout 2> $stderr
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    exit $exitCode
}
finally {
    Pop-Location
}
