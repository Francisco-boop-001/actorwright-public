import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import zipfile

import pytest


ROOT = Path(__file__).resolve().parents[1]
BOOTSTRAP_INTEGRATION = "ACTORWRIGHT_BOOTSTRAP_INTEGRATION"


def _run_bootstrap(root: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(root / "tools/scripts/bootstrap-dotnet10.ps1"),
        ],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )


def _fixture_repository(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    script = root / "tools/scripts/bootstrap-dotnet10.ps1"
    manifest = root / "tools/manifests/dotnet-sdk-10.0.301-win-x64.json"
    archive = root / "artifacts/tools/dotnet-sdk-10.0.301-win-x64.zip"
    build = tmp_path / "fixture-sdk"
    script.parent.mkdir(parents=True)
    manifest.parent.mkdir(parents=True)
    archive.parent.mkdir(parents=True)
    build.mkdir()
    shutil.copy2(ROOT / "tools/scripts/bootstrap-dotnet10.ps1", script)

    (build / "Program.cs").write_text(
        "using System; public static class Program { public static int Main(string[] args) { Console.WriteLine(\"10.0.301\"); return 0; } }",
        encoding="utf-8",
    )
    compile_result = subprocess.run(
        [
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-Command",
            "Add-Type -Path Program.cs -OutputAssembly dotnet.exe -OutputType ConsoleApplication",
        ],
        cwd=build,
        text=True,
        capture_output=True,
        check=False,
    )
    assert compile_result.returncode == 0, compile_result.stderr

    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as fixture_zip:
        fixture_zip.write(build / "dotnet.exe", "dotnet.exe")
    manifest.write_text(
        json.dumps(
            {
                "archive_sha512": hashlib.sha512(archive.read_bytes()).hexdigest(),
                "dotnet_sha256": hashlib.sha256((build / "dotnet.exe").read_bytes()).hexdigest(),
                "dotnet_length": (build / "dotnet.exe").stat().st_size,
                "source": "test fixture",
            }
        ),
        encoding="utf-8",
    )
    return root


def test_bootstrap_extracts_a_hash_verified_repository_local_sdk(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode == 0, bootstrap.stderr
    dotnet = root / "artifacts/tools/dotnet-sdk-10.0.301/dotnet.exe"
    assert dotnet.is_file()
    version = subprocess.run(
        [str(dotnet), "--version"],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )
    assert version.returncode == 0, version.stderr
    assert version.stdout.strip() == "10.0.301"


def test_bootstrap_refuses_a_hash_mismatched_archive_before_extraction(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    manifest = root / "tools/manifests/dotnet-sdk-10.0.301-win-x64.json"
    manifest.write_text('{"archive_sha512":"0"}', encoding="utf-8")

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode != 0
    assert "Pinned SDK archive hash mismatch" in bootstrap.stderr
    assert not (root / "artifacts/tools/dotnet-sdk-10.0.301").exists()


def test_bootstrap_uses_a_pinned_installed_sdk_without_the_archive(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    assert _run_bootstrap(root).returncode == 0
    (root / "artifacts/tools/dotnet-sdk-10.0.301-win-x64.zip").unlink()

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode == 0, bootstrap.stderr
    assert "(10.0.301)" in bootstrap.stdout


def test_bootstrap_refuses_an_extracted_sdk_with_wrong_executable_pin(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    manifest = root / "tools/manifests/dotnet-sdk-10.0.301-win-x64.json"
    pins = json.loads(manifest.read_text(encoding="utf-8"))
    pins["dotnet_sha256"] = "0" * 64
    manifest.write_text(json.dumps(pins), encoding="utf-8")

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode != 0
    assert "SDK executable hash mismatch" in bootstrap.stderr
    assert not (root / "artifacts/tools/dotnet-sdk-10.0.301").exists()


def test_bootstrap_replaces_an_installed_sdk_with_wrong_executable_length(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    assert _run_bootstrap(root).returncode == 0
    dotnet = root / "artifacts/tools/dotnet-sdk-10.0.301/dotnet.exe"
    dotnet.write_bytes(dotnet.read_bytes() + b"changed")

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode == 0, bootstrap.stderr
    pins = json.loads((root / "tools/manifests/dotnet-sdk-10.0.301-win-x64.json").read_text(encoding="utf-8"))
    assert dotnet.stat().st_size == pins["dotnet_length"]
    assert hashlib.sha256(dotnet.read_bytes()).hexdigest() == pins["dotnet_sha256"]


def test_bootstrap_replaces_an_installed_sdk_with_wrong_executable_hash(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    assert _run_bootstrap(root).returncode == 0
    dotnet = root / "artifacts/tools/dotnet-sdk-10.0.301/dotnet.exe"
    original = dotnet.read_bytes()
    dotnet.write_bytes(original[:-1] + bytes([original[-1] ^ 1]))
    version = subprocess.run(
        [str(dotnet), "--version"],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )
    assert version.returncode == 0, version.stderr
    assert version.stdout.strip() == "10.0.301"

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode == 0, bootstrap.stderr
    assert dotnet.read_bytes() == original


def test_bootstrap_replaces_a_partial_sdk_without_leaving_extract_staging(tmp_path: Path) -> None:
    root = _fixture_repository(tmp_path)
    install = root / "artifacts/tools/dotnet-sdk-10.0.301"
    install.mkdir(parents=True)
    (install / "dotnet.exe").write_bytes(b"partial executable")
    (install / "stale.txt").write_text("partial tree", encoding="utf-8")

    bootstrap = _run_bootstrap(root)

    assert bootstrap.returncode == 0, bootstrap.stderr
    assert not (install / "stale.txt").exists()
    assert not list(install.parent.glob("dotnet-sdk-10.0.301.stage-*"))


def test_bootstrap_real_sdk_integration_when_provisioned() -> None:
    if os.environ.get(BOOTSTRAP_INTEGRATION) != "1":
        pytest.skip(f"set {BOOTSTRAP_INTEGRATION}=1 after provisioning the pinned SDK archive")

    archive = ROOT / "artifacts/tools/dotnet-sdk-10.0.301-win-x64.zip"
    assert archive.is_file(), "the provisioned integration archive is missing"
    bootstrap = _run_bootstrap(ROOT)
    assert bootstrap.returncode == 0, bootstrap.stderr
    assert "(10.0.301)" in bootstrap.stdout


def test_current_docs_do_not_reference_the_former_workspace_layout() -> None:
    import re

    forbidden = (r"K:\ExampleWorkspace\projects", "02-normalized-resources", "05-reports/release-gate.json", "04-packages/npcmanager")
    historical = ("docs/releases/", "docs/superpowers/", "docs/migration/")
    hits = []
    for path in sorted((ROOT / "docs").rglob("*.md")) + [ROOT / "README.md"]:
        relative = path.relative_to(ROOT).as_posix()
        if relative.startswith(historical):
            continue
        text = path.read_text(encoding="utf-8")
        hits.extend(f"{relative}: {needle}" for needle in forbidden if needle in text)
        hits.extend(f"{relative}: stale executable name at offset {match.start()}" for match in re.finditer(r"^npcm ", text, re.MULTILINE))
    assert hits == [], "\n".join(hits)
