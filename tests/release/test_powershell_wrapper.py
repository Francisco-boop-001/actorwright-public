import importlib.util
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
WRAPPER = (
    b"$exe = Join-Path $PSScriptRoot 'actorwright.exe'; "
    b"if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { "
    b'[Console]::Error.WriteLine("Actorwright executable is missing: $exe"); exit 1 }; '
    b"& $exe @args; exit $LASTEXITCODE\r\n"
)


def verifier():
    spec = importlib.util.spec_from_file_location(
        "wrapper_release_verifier", ROOT / "tools/release/verify_release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def release_fixture(root):
    check = verifier()
    for directory in check.REQUIRED_DIRS:
        (root / directory).mkdir(parents=True)
    for filename in check.REQUIRED_FILES:
        (root / filename).write_bytes(b"{}")
    for filename in ("cli/actorwright.exe", "cli/npcm.cmd",
                     "desktop/Actorwright.Desktop.exe"):
        (root / filename).write_bytes(b"synthetic inventory member")
    return check


def test_release_accepts_only_the_declared_powershell_wrapper(tmp_path):
    check = release_fixture(tmp_path)
    wrapper = tmp_path / "cli/actorwright.ps1"
    wrapper.write_bytes(WRAPPER)
    assert check.inventory(tmp_path)["cli/actorwright.ps1"] == wrapper
    wrapper.write_bytes(b"Write-Output 'unexpected code'\n")
    with pytest.raises(check.ReleaseError, match="PowerShell wrapper"):
        check.inventory(tmp_path)
    wrapper.write_bytes(WRAPPER)
    (tmp_path / "docs/other.ps1").write_bytes(WRAPPER)
    with pytest.raises(check.ReleaseError, match="source, test"):
        check.inventory(tmp_path)


def test_release_requires_powershell_wrapper(tmp_path):
    check = release_fixture(tmp_path)
    with pytest.raises(check.ReleaseError, match="PowerShell wrapper"):
        check.verify_release_wrapper(check.inventory(tmp_path), "1.0.0-preview.267")


def test_frozen_preview266_release_does_not_require_new_wrapper(tmp_path):
    check = release_fixture(tmp_path)
    check.verify_release_wrapper(check.inventory(tmp_path), "1.0.0-preview.266")


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
def test_built_wrapper_preserves_pipe_arguments_and_exit_code(tmp_path):
    # Execute the actual emission statement, without running/tagging a release.
    lines = (ROOT / "tools/release/build_release.ps1").read_text().splitlines()
    emit = [line for line in lines if "cli\\actorwright.ps1" in line]
    assert len(emit) == 1, "release builder must emit the PowerShell wrapper"
    (tmp_path / "cli").mkdir()
    path = str(tmp_path).replace("'", "''")
    script = tmp_path / "probe.ps1"
    script.write_text("\n".join([
        "$ErrorActionPreference = 'Stop'",
        f"$OutputRoot = '{path}'",
        emit[0],
        "Add-Type -OutputType ConsoleApplication -OutputAssembly (Join-Path $OutputRoot 'cli/actorwright.exe') -TypeDefinition @'",
        "using System; public static class Probe { public static int Main(string[] args) { foreach (var arg in args) Console.WriteLine(arg); return 37; } }",
        "'@",
        "& (Join-Path $OutputRoot 'cli/actorwright.ps1') 'Provider Name.esp|0x00000800' 'a|b c' '--json'",
        "exit $LASTEXITCODE",
    ]), encoding="utf-8")
    result = subprocess.run([
        "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)
    ], capture_output=True, text=True, timeout=60)
    assert result.returncode == 37, result.stdout + result.stderr
    assert result.stdout.splitlines() == ["Provider Name.esp|0x00000800", "a|b c", "--json"]
    assert (tmp_path / "cli/actorwright.ps1").read_bytes() == WRAPPER


@pytest.mark.skipif(not shutil.which("powershell.exe"), reason="Windows PowerShell required")
def test_built_wrapper_fails_clearly_when_executable_is_missing(tmp_path):
    lines = (ROOT / "tools/release/build_release.ps1").read_text().splitlines()
    emit = [line for line in lines if "cli\\actorwright.ps1" in line]
    assert len(emit) == 1, "release builder must emit the PowerShell wrapper"
    (tmp_path / "cli").mkdir()
    path = str(tmp_path).replace("'", "''")
    script = tmp_path / "probe.ps1"
    script.write_text("\n".join([
        "$ErrorActionPreference = 'Stop'",
        f"$OutputRoot = '{path}'",
        emit[0],
        "& (Join-Path $OutputRoot 'cli/actorwright.ps1') '--json'",
        "exit $LASTEXITCODE",
    ]), encoding="utf-8")

    result = subprocess.run([
        "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script)
    ], capture_output=True, text=True, timeout=60)

    assert result.returncode == 1, result.stdout + result.stderr
    assert "Actorwright executable is missing:" in result.stderr
