import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
RELEASE_SCRIPT = ROOT / "tools" / "release" / "build_release.ps1"
SOURCE_VALIDATOR = ROOT / "tools" / "architecture" / "validate_source_cleanliness.py"
REFUSAL = "Release source has working-tree changes"


def _run(
    *args: str,
    cwd: Path,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        list(args),
        cwd=cwd,
        env=env,
        text=True,
        capture_output=True,
        check=False,
    )


def test_release_preflight_blocks_dirty_source_and_an_occupied_target_tag(
    tmp_path: Path,
) -> None:
    repository = tmp_path / "repository"
    release_dir = repository / "tools" / "release"
    build_dir = repository / "tools" / "build"
    release_dir.mkdir(parents=True)
    build_dir.mkdir(parents=True)
    shutil.copy2(RELEASE_SCRIPT, release_dir / RELEASE_SCRIPT.name)

    build_marker = tmp_path / "build.marker"
    package_marker = tmp_path / "package.marker"
    (build_dir / "build.ps1").write_text(
        "Set-Content -LiteralPath $env:ACTORWRIGHT_TEST_BUILD_MARKER -Value reached\n"
        "Write-Output '1 passed in 0.01s'\n",
        encoding="utf-8",
    )
    (build_dir / "package.ps1").write_text(
        "Set-Content -LiteralPath $env:ACTORWRIGHT_TEST_PACKAGE_MARKER -Value reached\n"
        "throw 'Package marker reached'\n",
        encoding="utf-8",
    )
    fake_dotnet = repository / "dotnet.exe"
    fake_dotnet.write_bytes(b"")

    git_config = tmp_path / "gitconfig"
    config_result = _run(
        "git",
        "config",
        "--file",
        str(git_config),
        "safe.directory",
        repository.as_posix(),
        cwd=repository,
    )
    assert config_result.returncode == 0, config_result.stdout + config_result.stderr
    environment = os.environ.copy()
    environment["GIT_CONFIG_GLOBAL"] = str(git_config)

    for command in (
        ("git", "init"),
        ("git", "config", "user.email", "release-test@example.invalid"),
        ("git", "config", "user.name", "Actorwright Release Test"),
        ("git", "add", "."),
        ("git", "commit", "-m", "release source"),
    ):
        result = _run(*command, cwd=repository, env=environment)
        assert result.returncode == 0, result.stdout + result.stderr

    (repository / "UntrackedCompileInput.cs").write_text(
        "internal static class UntrackedCompileInput {}\n",
        encoding="utf-8",
    )

    powershell = shutil.which("powershell.exe")
    assert powershell is not None
    environment["ACTORWRIGHT_TEST_BUILD_MARKER"] = str(build_marker)
    environment["ACTORWRIGHT_TEST_PACKAGE_MARKER"] = str(package_marker)
    result = subprocess.run(
        [
            powershell,
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(release_dir / RELEASE_SCRIPT.name),
            "-DotNetPath",
            str(fake_dotnet),
            "-OutputRoot",
            str(tmp_path / "release-output"),
            "-ZipPath",
            str(tmp_path / "release-output.zip"),
        ],
        cwd=repository,
        env=environment,
        text=True,
        capture_output=True,
        check=False,
    )

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert REFUSAL in output
    assert not build_marker.exists()
    assert not package_marker.exists()

    tag = _run(
        "git",
        "show-ref",
        "--verify",
        "--quiet",
        "refs/tags/v1.0.0-preview.281",
        cwd=repository,
        env=environment,
    )
    assert tag.returncode == 1

    (repository / "UntrackedCompileInput.cs").unlink()
    tagged = _run(
        "git",
        "tag",
        "v1.0.0-preview.281",
        cwd=repository,
        env=environment,
    )
    assert tagged.returncode == 0, tagged.stdout + tagged.stderr
    result = _run(
        powershell,
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(release_dir / RELEASE_SCRIPT.name),
        "-DotNetPath",
        str(fake_dotnet),
        "-OutputRoot",
        str(tmp_path / "release-output"),
        "-ZipPath",
        str(tmp_path / "release-output.zip"),
        cwd=repository,
        env=environment,
    )

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert "Release tag already exists: v1.0.0-preview.281" in output
    assert not build_marker.exists()
    assert not package_marker.exists()

    private = _run(
        powershell,
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(release_dir / RELEASE_SCRIPT.name),
        "-PrivateNoTag",
        "-DotNetPath",
        str(fake_dotnet),
        "-OutputRoot",
        str(tmp_path / "release-output"),
        "-ZipPath",
        str(tmp_path / "release-output.zip"),
        cwd=repository,
        env=environment,
    )
    assert private.returncode != 0
    assert "Canonical build output must contain exactly one EVIDENCE_SOURCE_COMMIT stamp" in (
        private.stdout + private.stderr)
    assert build_marker.exists()
    assert not package_marker.exists()


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
@pytest.mark.parametrize(
    "private,verification_json,verification_exit,should_pass",
    [
        (True, {"status": "PASS", "zipVerified": True,
                "compatibilityVerdict": "FULL_COMPATIBLE",
                "sourceCommit": "a" * 40, "version": "1.0.0-preview.281"}, 0, True),
        (True, {"status": "PASS", "zipVerified": "true",
                "compatibilityVerdict": "FULL_COMPATIBLE",
                "sourceCommit": "a" * 40, "version": "1.0.0-preview.281"}, 0, False),
        (True, {"status": "PASS", "zipVerified": True,
                "compatibilityVerdict": "PARTIAL",
                "sourceCommit": "a" * 40, "version": "1.0.0-preview.281"}, 0, False),
        (True, {"status": "PASS", "zipVerified": True,
                "compatibilityVerdict": "FULL_COMPATIBLE",
                "sourceCommit": "b" * 40, "version": "1.0.0-preview.281"}, 0, False),
        (True, [{"status": "PASS", "zipVerified": True,
                 "compatibilityVerdict": "FULL_COMPATIBLE",
                 "sourceCommit": "a" * 40, "version": "1.0.0-preview.281"}], 0, False),
        (True, {"status": "PASS", "zipVerified": True,
                "compatibilityVerdict": "FULL_COMPATIBLE",
                "sourceCommit": "a" * 40, "version": "1.0.0-preview.281"}, 17, False),
        (False, {}, 0, True),
    ],
)
def test_release_terminal_routes_private_verification_without_tag_helper(
    tmp_path: Path,
    private: bool,
    verification_json: object,
    verification_exit: int,
    should_pass: bool,
) -> None:
    # Execute the actual terminal statements with controlled external commands.
    # The costly build and package assembly happen before this boundary.
    script = RELEASE_SCRIPT.read_text(encoding="utf-8")
    boundary = "$hashLines | Set-Content -LiteralPath (Join-Path $OutputRoot 'SHA256SUMS') -Encoding ASCII"
    assert script.count(boundary) == 1
    terminal = script.split(boundary, 1)[1]
    release_dir = tmp_path / "tools" / "release"
    release_dir.mkdir(parents=True)
    verifier_args = tmp_path / "verifier-args.json"
    tag_marker = tmp_path / "tag-helper.marker"
    (release_dir / "verify_release.py").write_text(
        "import json, os, pathlib, sys\n"
        "pathlib.Path(os.environ['ACTORWRIGHT_VERIFIER_ARGS']).write_text(json.dumps(sys.argv[1:]))\n"
        "print(os.environ['ACTORWRIGHT_VERIFIER_RESPONSE'])\n"
        "raise SystemExit(int(os.environ['ACTORWRIGHT_VERIFIER_EXIT']))\n",
        encoding="utf-8",
    )
    (release_dir / "verify_and_tag.ps1").write_text(
        "Set-Content -LiteralPath $env:ACTORWRIGHT_TAG_MARKER -Value invoked\n",
        encoding="utf-8",
    )
    harness = release_dir / "terminal-harness.ps1"
    harness.write_text(
        "param([switch]$PrivateNoTag)\n"
        "$ErrorActionPreference = 'Stop'\n"
        "$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\\..')).Path\n"
        f"$python = '{str(sys.executable).replace(chr(39), chr(39) * 2)}'\n"
        "$git = 'git.exe'\n"
        "$OutputRoot = Join-Path $projectRoot 'release-root'\n"
        "$ZipPath = Join-Path $projectRoot 'release.zip'\n"
        "$tag = 'v1.0.0-preview.281'\n"
        "$commit = '" + "a" * 40 + "'\n"
        "$version = '1.0.0-preview.281'\n"
        + terminal,
        encoding="utf-8",
    )
    environment = os.environ.copy()
    environment["ACTORWRIGHT_VERIFIER_ARGS"] = str(verifier_args)
    environment["ACTORWRIGHT_VERIFIER_RESPONSE"] = json.dumps(verification_json)
    environment["ACTORWRIGHT_VERIFIER_EXIT"] = str(verification_exit)
    environment["ACTORWRIGHT_TAG_MARKER"] = str(tag_marker)
    command = [shutil.which("powershell.exe") or "powershell.exe",
               "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
               str(harness)]
    if private:
        command.append("-PrivateNoTag")
    result = _run(*command, cwd=tmp_path, env=environment)
    output = result.stdout + result.stderr
    if should_pass:
        assert result.returncode == 0, output
        assert "Actorwright deterministic ZIP: PASS" in output
    else:
        assert result.returncode != 0, output
        assert "Actorwright deterministic ZIP: PASS" not in output
    if private:
        assert not tag_marker.exists()
        assert json.loads(verifier_args.read_text(encoding="utf-8")) == [
            str(tmp_path / "release-root"), "--create-zip",
            str(tmp_path / "release.zip"), "--json"]
    else:
        assert tag_marker.exists()
        assert not verifier_args.exists()


def test_effective_src_compile_inputs_must_be_tracked_and_present(
    tmp_path: Path,
) -> None:
    dotnet = _dotnet_path()
    if dotnet is None:
        pytest.skip("dotnet SDK is unavailable")

    repository = tmp_path / "repository"
    _write(
        repository,
        "src/Default/Default.csproj",
        """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
</Project>
""",
    )
    _write(
        repository,
        "src/Missing/Missing.csproj",
        """<Project Sdk=\"Microsoft.NET.Sdk\">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup><Compile Include=\"Missing.cs\" /></ItemGroup>
</Project>
""",
    )
    _write(repository, ".gitignore", "/src/Default/Ignored.cs\n")
    _write(repository, "src/Default/Default.cs", "internal static class Default {}\n")
    _git_initialize(repository)
    clean = _run("git", "status", "--porcelain", cwd=repository)
    assert clean.returncode == 0, clean.stdout + clean.stderr
    assert clean.stdout == ""
    _write(repository, "src/Default/Untracked.cs", "internal static class Untracked {}\n")
    _write(repository, "src/Default/Ignored.cs", "internal static class Ignored {}\n")

    environment = os.environ.copy()
    environment["GIT_CONFIG_GLOBAL"] = str(tmp_path / "gitconfig")
    dotnet_home = tmp_path / "dotnet-home"
    dotnet_home.mkdir()
    environment["DOTNET_CLI_HOME"] = str(dotnet_home)
    environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    environment["DOTNET_NOLOGO"] = "1"
    environment.pop("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", None)
    result = subprocess.run(
        [
            shutil.which("python") or "python",
            str(SOURCE_VALIDATOR),
            "--repository-root",
            str(repository),
            "--dotnet",
            str(dotnet),
        ],
        cwd=repository,
        env=environment,
        text=True,
        capture_output=True,
        check=False,
    )

    output = result.stdout + result.stderr
    assert result.returncode != 0
    assert "src/Default/Untracked.cs" in output
    assert "untracked" in output
    assert "src/Default/Ignored.cs" in output
    assert "ignored" in output
    assert "src/Missing/Missing.cs" in output
    assert "missing" in output
    assert "MSBuild Compile evaluation was not JSON" not in output


def test_msbuild_compile_evaluation_accepts_cli_boundary_before_json(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    """The MSBuild query may include a logger boundary before its JSON value."""
    module_spec = importlib.util.spec_from_file_location(
        "validate_source_cleanliness",
        SOURCE_VALIDATOR,
    )
    assert module_spec is not None and module_spec.loader is not None
    validator = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(validator)

    root = tmp_path / "repository"
    project = root / "src" / "Application" / "Application.csproj"
    project.parent.mkdir(parents=True)
    project.write_text("<Project />\n", encoding="utf-8")
    compile_path = project.parent / "Application.cs"
    msbuild_output = (
        "\ufeffMSBuild version 17.14.8+... for .NET\r\n"
        + json.dumps({"Items": {"Compile": [{"FullPath": str(compile_path)}]}})
        + "\r\n"
    )
    monkeypatch.setattr(
        validator,
        "_run",
        lambda args, *, cwd: subprocess.CompletedProcess(
            args,
            0,
            stdout=msbuild_output,
            stderr="",
        ),
    )

    paths, error = validator._project_compile_items(
        project,
        dotnet=tmp_path / "dotnet.exe",
        root=root,
    )

    assert error is None
    assert paths == [str(compile_path)]


def test_msbuild_compile_evaluation_rejects_unrelated_json_boundary(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    module_spec = importlib.util.spec_from_file_location(
        "validate_source_cleanliness",
        SOURCE_VALIDATOR,
    )
    assert module_spec is not None and module_spec.loader is not None
    validator = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(validator)

    root = tmp_path / "repository"
    project = root / "src" / "Application" / "Application.csproj"
    project.parent.mkdir(parents=True)
    project.write_text("<Project />\n", encoding="utf-8")
    msbuild_output = (
        "MSBuild version 17.14.8+... for .NET\r\n"
        + json.dumps({"Properties": {"Compile": "not-an-item-list"}})
        + "\r\n"
    )
    monkeypatch.setattr(
        validator,
        "_run",
        lambda args, *, cwd: subprocess.CompletedProcess(
            args,
            0,
            stdout=msbuild_output,
            stderr="",
        ),
    )

    paths, error = validator._project_compile_items(
        project,
        dotnet=tmp_path / "dotnet.exe",
        root=root,
    )

    assert paths == []
    assert error is not None
    assert "invalid item payload" in error


@pytest.mark.parametrize(
    "framing",
    ["extra-array", "extra-scalar", "wrapped-object"],
)
def test_msbuild_compile_evaluation_rejects_more_than_one_top_level_object(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    framing: str,
) -> None:
    """Only one top-level Items object may cross the MSBuild boundary."""
    module_spec = importlib.util.spec_from_file_location(
        "validate_source_cleanliness",
        SOURCE_VALIDATOR,
    )
    assert module_spec is not None and module_spec.loader is not None
    validator = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(validator)

    root = tmp_path / "repository"
    project = root / "src" / "Application" / "Application.csproj"
    project.parent.mkdir(parents=True)
    project.write_text("<Project />\n", encoding="utf-8")
    compile_path = project.parent / "Application.cs"
    payload = {
        "Items": {"Compile": [{"FullPath": str(compile_path)}]},
    }
    if framing == "extra-array":
        msbuild_output = json.dumps(payload) + "\n[1]\n"
    elif framing == "extra-scalar":
        msbuild_output = json.dumps(payload) + "\n42\n"
    else:
        msbuild_output = "MSBuild wrapper: " + json.dumps([payload]) + "\n"
    monkeypatch.setattr(
        validator,
        "_run",
        lambda args, *, cwd: subprocess.CompletedProcess(
            args,
            0,
            stdout=msbuild_output,
            stderr="",
        ),
    )

    paths, error = validator._project_compile_items(
        project,
        dotnet=tmp_path / "dotnet.exe",
        root=root,
    )

    assert paths == []
    assert error is not None
    assert "was not JSON" in error


def test_canonical_build_invokes_source_cleanliness_before_restore() -> None:
    build_script = (ROOT / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8"
    )
    validator = "tools\\architecture\\validate_source_cleanliness.py"
    assert validator in build_script
    assert build_script.index(validator) < build_script.index("& $dotnet @restoreArguments")
    assert "[string]$RestoreConfigFile" in build_script
    assert "[switch]$RequireCleanTree" in build_script
    assert "[switch]$NoHttpCache" in build_script
    assert "rev-parse HEAD" in build_script
    assert "rev-parse 'HEAD^{tree}'" in build_script
    assert "status --porcelain" in build_script
    assert "EVIDENCE_SOURCE_COMMIT=" in build_script
    assert "EVIDENCE_SOURCE_TREE=" in build_script
    assert "EVIDENCE_WORKTREE_STATUS=" in build_script
    assert "EVIDENCE_DOTNET_PATH=" in build_script
    assert "EVIDENCE_DOTNET_SDK_VERSION=" in build_script
    assert "global.json" in build_script
    assert "--git-common-dir" in build_script
    assert "Pinned .NET SDK version mismatch" in build_script
    assert "EVIDENCE_RESTORE_CONFIG_SHA256=" in build_script
    assert "EVIDENCE_TRACKED_NUGET_CONFIG_SHA256=" in build_script
    assert "EVIDENCE_NUGET_HTTP_CACHE=" in build_script
    assert "EVIDENCE_FINAL_SOURCE_COMMIT=" in build_script
    assert "EVIDENCE_FINAL_SOURCE_TREE=" in build_script
    assert "EVIDENCE_FINAL_WORKTREE_STATUS=" in build_script
    assert "EVIDENCE_FINAL_RESTORE_CONFIG_SHA256=" in build_script
    assert "--configfile" in build_script


def test_release_builder_reuses_the_canonical_builds_pinned_sdk() -> None:
    release_script = RELEASE_SCRIPT.read_text(encoding="utf-8")
    build_invocation = "$buildOutput = & powershell.exe @buildArguments"
    selected_sdk = "Get-BuildEvidenceValue 'EVIDENCE_DOTNET_PATH'"

    assert "(Get-Command dotnet.exe" not in release_script
    assert "if ($DotNetPath) {" in release_script
    assert "$buildArguments += @('-DotNetPath', $dotnetOverride)" in release_script
    assert selected_sdk in release_script
    assert "$dotnet = (Resolve-Path -LiteralPath $buildDotNetPath).Path" in release_script
    assert release_script.index(build_invocation) < release_script.index(selected_sdk)
    assert release_script.index(selected_sdk) < release_script.index(
        "tools\\build\\package.ps1"
    )


def test_release_builder_allows_the_canonical_build_to_create_its_default_cache() -> None:
    release_script = RELEASE_SCRIPT.read_text(encoding="utf-8")
    default_cache = "[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\\nuget-packages'))"
    cache_check = "Test-Path -LiteralPath $packages -PathType Container"
    vulnerability_scan = "tools\\hardening\\dependency_vulnerability_scan.ps1"

    assert default_cache in release_script
    assert cache_check in release_script
    assert release_script.index(cache_check) < release_script.index(vulnerability_scan)


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
def test_release_builder_rejects_transport_provenance_without_restore_config() -> None:
    result = _run(
        shutil.which("powershell.exe") or "powershell.exe",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(RELEASE_SCRIPT),
        "-AuditTransportProvenance",
        "K:\\Actorwright\\provenance.json",
        cwd=ROOT,
    )

    assert result.returncode != 0
    assert "AuditTransportProvenance requires RestoreConfigFile" in (
        result.stdout + result.stderr
    )


def test_release_builder_propagates_exact_audit_transport_inputs_to_scanner() -> None:
    release_script = RELEASE_SCRIPT.read_text(encoding="utf-8")
    scanner = release_script.index("$transportArguments = @()")
    validator = release_script.index("validate_dependency_vulnerability_report.py")
    invocation = release_script[scanner:validator]

    assert "[string]$AuditTransportProvenance" in release_script
    assert "if ($AuditTransportProvenance -and -not $RestoreConfigFile)" in release_script
    assert "tools\\hardening\\dependency_vulnerability_scan.ps1" in invocation
    assert "'-RestoreConfigFile', $RestoreConfigFile" in invocation
    assert "'-AuditTransportProvenance', $AuditTransportProvenance" in invocation


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
@pytest.mark.parametrize("exit_code, should_succeed", [(0, True), (17, False)])
def test_release_builder_uses_canonical_build_exit_code_when_stderr_is_present(
    tmp_path: Path,
    exit_code: int,
    should_succeed: bool,
) -> None:
    lines = RELEASE_SCRIPT.read_text(encoding="utf-8").splitlines()
    restore_start = lines.index("if ($RestoreConfigFile) {")
    block_start = lines.index("}", restore_start) + 1
    block_end = next(
        index for index, line in enumerate(lines)
        if line.startswith("$buildText = ")
    ) + 1
    capture_block = "\n".join(lines[block_start:block_end])

    child = tmp_path / "child.ps1"
    child.write_text(
        'Write-Output "EVIDENCE_SOURCE_COMMIT=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"\n'
        '[Console]::Error.WriteLine("native diagnostic")\n'
        f"exit {exit_code}\n",
        encoding="utf-8",
    )
    harness = tmp_path / "harness.ps1"
    child_path = str(child).replace("'", "''")
    harness.write_text(
        "$ErrorActionPreference = 'Stop'\n"
        f"$buildArguments = @('-NoProfile', '-File', '{child_path}')\n"
        f"{capture_block}\n"
        "if (@($buildOutput | Where-Object { $_ -isnot [string] }).Count -ne 0) { "
        "throw 'CAPTURE_CONTAINS_NON_STRING' }\n"
        "if ([regex]::Matches($buildText, "
        "'(?m)^EVIDENCE_SOURCE_COMMIT=[A-F0-9]{40}$').Count -ne 1) { "
        "throw 'CAPTURE_EVIDENCE_CORRUPTED' }\n"
        "Write-Output 'CAPTURE_COMPLETED'\n",
        encoding="utf-8",
    )

    result = _run(
        shutil.which("powershell.exe") or "powershell.exe",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        str(harness),
        cwd=tmp_path,
    )
    output = result.stdout + result.stderr
    if should_succeed:
        assert result.returncode == 0, output
        assert "native diagnostic" in output
        assert "CAPTURE_COMPLETED" in output
    else:
        assert result.returncode != 0
        assert f"Canonical build failed with exit code {exit_code}" in output
        assert "CAPTURE_COMPLETED" not in output


def test_repository_normalizes_text_before_commit() -> None:
    attributes = (ROOT / ".gitattributes").read_text(encoding="utf-8").splitlines()
    assert attributes[0] == "* text=auto"


def test_canonical_build_propagates_resolved_dotnet_to_python_fixture_handoff() -> None:
    build_script = (ROOT / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8"
    )
    handoff = "$env:ACTORWRIGHT_TEST_DOTNET = $dotnet"
    source_validator_invocation = "& $python $sourceValidator"
    pytest_invocation = "& $python -m pytest"
    assert handoff in build_script
    assert build_script.index(handoff) < build_script.index(
        source_validator_invocation
    )
    assert build_script.index(handoff) < build_script.index(pytest_invocation)


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
def test_canonical_pytest_scopes_temporary_git_trust_and_restores_environment(
    tmp_path: Path,
) -> None:
    build_script = (ROOT / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8"
    )
    start = build_script.index("$pytestBase = ")
    end = build_script.index('Write-Output "Actorwright standalone private build:', start)
    pytest_block = build_script[start:end]
    support_tools = tmp_path / "tools"
    support_tools.mkdir()
    shutil.copy2(
        ROOT / "tools" / "storage_retention.py",
        support_tools / "storage_retention.py",
    )

    for preexisting in (False, True):
        for exit_code in (0, 17):
            harness = tmp_path / f"pytest-git-trust-{preexisting}-{exit_code}.ps1"
            harness.write_text(
                "$ErrorActionPreference = 'Stop'\n"
                f"$artifacts = Join-Path '{tmp_path}' 'artifacts'\n"
                f"$projectRoot = '{tmp_path}'\n"
                "$python = 'Invoke-FakePython'\n"
                "$realPython = (Get-Command python.exe -ErrorAction Stop).Source\n"
                f"$expectedExit = {exit_code}\n"
                f"$preexisting = ${str(preexisting).lower()}\n"
                "$artifactSibling = Join-Path $artifacts 'keep'\n"
                f"$outsideRoot = Join-Path '{tmp_path}' 'outside'\n"
                "New-Item -ItemType Directory -Force -Path $artifacts, $artifactSibling, $outsideRoot | Out-Null\n"
                "Set-Content -LiteralPath (Join-Path $artifactSibling 'sentinel.txt') -Value 'keep'\n"
                "Set-Content -LiteralPath (Join-Path $outsideRoot 'sentinel.txt') -Value 'keep'\n"
                "if ($preexisting) {\n"
                "    $env:GIT_CONFIG_COUNT = '7'\n"
                "    $env:GIT_CONFIG_KEY_0 = 'prior.key'\n"
                "    $env:GIT_CONFIG_VALUE_0 = 'prior-value'\n"
                "} else {\n"
                "    Remove-Item Env:GIT_CONFIG_COUNT, Env:GIT_CONFIG_KEY_0, Env:GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue\n"
                "}\n"
                "function Invoke-FakePython {\n"
                "    if ($args.Count -gt 0 -and $args[0] -eq '-c') {\n"
                "        & $realPython @args\n"
                "        $global:LASTEXITCODE = $LASTEXITCODE\n"
                "        return\n"
                "    }\n"
                "    if ($env:GIT_CONFIG_COUNT -ne '1' -or\n"
                "        $env:GIT_CONFIG_KEY_0 -ne 'safe.directory' -or\n"
                "        $env:GIT_CONFIG_VALUE_0 -ne '*') { throw 'PYTEST_GIT_TRUST_MISSING' }\n"
                "    $baseIndex = [Array]::IndexOf($args, '--basetemp')\n"
                "    if ($baseIndex -lt 0 -or $baseIndex + 1 -ge $args.Count) { throw 'PYTEST_BASETEMP_MISSING' }\n"
                "    $script:pytestBase = [string]$args[$baseIndex + 1]\n"
                "    New-Item -ItemType Directory -Force -Path $script:pytestBase | Out-Null\n"
                "    Set-Content -LiteralPath (Join-Path $script:pytestBase 'fixture.txt') -Value 'scratch'\n"
                "    $readonlyFile = Join-Path $script:pytestBase 'readonly.txt'\n"
                "    Set-Content -LiteralPath $readonlyFile -Value 'scratch'\n"
                "    $readonlyItem = Get-Item -LiteralPath $readonlyFile -Force\n"
                "    $readonlyItem.Attributes = $readonlyItem.Attributes -bor [IO.FileAttributes]::ReadOnly\n"
                "    $global:LASTEXITCODE = $expectedExit\n"
                "}\n"
                "try {\n"
                f"{pytest_block}\n"
                "    if ($expectedExit -ne 0) { throw 'PYTEST_FAILURE_NOT_PROPAGATED' }\n"
                "} catch {\n"
                "    if ($expectedExit -eq 0 -or $_.Exception.Message -notmatch 'fixture tests failed') { throw }\n"
                "    if ($_.Exception.Message -notlike \"*$pytestBase*\") { throw 'PYTEST_FAILURE_PATH_NOT_REPORTED' }\n"
                "}\n"
                "if ($expectedExit -eq 0) {\n"
                "    if (Test-Path -LiteralPath $pytestBase) { throw 'PYTEST_SUCCESS_TEMP_NOT_CLEANED' }\n"
                "} elseif (-not (Test-Path -LiteralPath (Join-Path $pytestBase 'fixture.txt'))) {\n"
                "    throw 'PYTEST_FAILURE_TEMP_NOT_PRESERVED'\n"
                "}\n"
                "if (-not (Test-Path -LiteralPath (Join-Path $artifactSibling 'sentinel.txt')) -or\n"
                "    -not (Test-Path -LiteralPath (Join-Path $outsideRoot 'sentinel.txt'))) { throw 'UNOWNED_OUTPUT_REMOVED' }\n"
                "if ($preexisting) {\n"
                "    if ($env:GIT_CONFIG_COUNT -ne '7' -or $env:GIT_CONFIG_KEY_0 -ne 'prior.key' -or\n"
                "        $env:GIT_CONFIG_VALUE_0 -ne 'prior-value') { throw 'PRIOR_GIT_CONFIG_NOT_RESTORED' }\n"
                "} elseif ($null -ne $env:GIT_CONFIG_COUNT -or $null -ne $env:GIT_CONFIG_KEY_0 -or\n"
                "          $null -ne $env:GIT_CONFIG_VALUE_0) { throw 'GIT_CONFIG_NOT_REMOVED' }\n"
                "Write-Output 'PYTEST_GIT_TRUST_SCOPED'\n",
                encoding="utf-8",
            )
            result = _run(
                shutil.which("powershell.exe") or "powershell.exe",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                str(harness),
                cwd=tmp_path,
            )
            assert result.returncode == 0, result.stdout + result.stderr
            assert "PYTEST_GIT_TRUST_SCOPED" in result.stdout


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
def test_canonical_build_isolates_nuget_http_cache_before_restore(
    tmp_path: Path,
) -> None:
    artifacts = tmp_path / "artifacts"
    build_script = ROOT / "tools" / "build" / "build.ps1"
    command = rf"""
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    '{build_script}', [ref]$tokens, [ref]$errors)
$function = $ast.Find({{
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Set-CanonicalNuGetHttpCache'
}}, $true)
if ($null -eq $function) {{ throw 'production cache initializer missing' }}
Invoke-Expression $function.Extent.Text
$env:NUGET_HTTP_CACHE_PATH = 'C:\Users\someone\AppData\Local\NuGet\v3-cache'
Set-CanonicalNuGetHttpCache '{artifacts}'
Write-Output $env:NUGET_HTTP_CACHE_PATH
"""
    result = _run(
        shutil.which("powershell.exe") or "powershell.exe",
        "-NoProfile",
        "-Command",
        command,
        cwd=ROOT,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    expected = artifacts / "nuget-http-cache"
    assert Path(result.stdout.strip()) == expected
    assert expected.is_dir()


def _dotnet_path() -> Path | None:
    configured = os.environ.get("ACTORWRIGHT_TEST_DOTNET")
    if configured:
        path = Path(configured)
        if path.is_file():
            return path
    pinned = (
        ROOT.parent / "bounded-readiness-repairs" / "artifacts" /
        "dotnet-sdk-10.0.301" / "dotnet.exe"
    )
    if pinned.is_file():
        return pinned
    discovered = shutil.which("dotnet")
    return Path(discovered) if discovered else None


def _write(root: Path, relative: str, text: str) -> None:
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8")


def _git_initialize(repository: Path) -> None:
    git_config = repository.parent / "gitconfig"
    environment = os.environ.copy()
    environment["GIT_CONFIG_GLOBAL"] = str(git_config)
    commands = (
        ("git", "config", "--file", str(git_config), "safe.directory", repository.as_posix()),
        ("git", "init"),
        ("git", "config", "user.email", "source-cleanliness@example.invalid"),
        ("git", "config", "user.name", "Actorwright Source Cleanliness"),
        ("git", "add", ".gitignore", "src/Default/Default.csproj", "src/Default/Default.cs", "src/Missing/Missing.csproj"),
        ("git", "commit", "-m", "release-shaped source"),
    )
    for command in commands:
        result = _run(*command, cwd=repository, env=environment)
        assert result.returncode == 0, result.stdout + result.stderr
