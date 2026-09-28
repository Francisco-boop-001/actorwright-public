import hashlib
import json
import shutil
import subprocess
import uuid
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
WRITER = ROOT / "tools" / "build" / "write_package_verification_evidence.ps1"
POWERSHELL = shutil.which("powershell") or shutil.which("powershell.exe")


def _invoke(
    package_root: Path,
    verifier_result: str,
    *extra_args: str,
) -> subprocess.CompletedProcess[str]:
    assert POWERSHELL is not None, "Windows PowerShell is required"
    return subprocess.run(
        [
            POWERSHELL,
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            str(WRITER),
            "-PackageRoot",
            str(package_root),
            "-VerificationJson",
            verifier_result,
            *extra_args,
        ],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )


def _package_fixture(tmp_path: Path, suffix: str) -> Path:
    package_root = tmp_path / f"actorwright-observability-{suffix}-{uuid.uuid4().hex}"
    package_root.mkdir()
    (package_root / "manifest.json").write_text(
        '{"schemaVersion":1,"product":"Actorwright"}\n',
        encoding="utf-8",
    )
    return package_root


def _valid_verifier_result() -> dict[str, object]:
    return {
        "artifactKind": "binary-package-staging",
        "files": 6,
        "protocolV2Kernel": True,
        "status": "PASS",
        "version": "1.0.0-preview.260",
        "zipVerified": False,
    }


def _remove_owned_evidence(evidence_root: Path, leaf: str) -> None:
    if not evidence_root.is_dir():
        return
    owned_paths = [evidence_root / f"{leaf}.json"]
    owned_paths.extend(evidence_root.glob(f".{leaf}.json.tmp-*"))
    for owned_path in owned_paths:
        assert owned_path.parent == evidence_root
        owned_path.unlink(missing_ok=True)


def test_persists_only_admitted_verifier_pass_outside_sealed_package(
    tmp_path: Path,
) -> None:
    evidence_root = ROOT / "artifacts" / "package-verification"
    owned_leaves: set[str] = set()
    try:
        valid_result = _valid_verifier_result()
        valid_json = json.dumps(valid_result, separators=(",", ":"), sort_keys=True)
        package_root = _package_fixture(tmp_path, "pass")
        owned_leaves.add(package_root.name)
        evidence_path = evidence_root / f"{package_root.name}.json"
        package_inventory_before = sorted(path.name for path in package_root.iterdir())

        written = _invoke(package_root, valid_json)
        assert written.returncode == 0, written.stderr
        assert evidence_path.is_file()
        assert sorted(path.name for path in package_root.iterdir()) == package_inventory_before

        evidence_bytes = evidence_path.read_bytes()
        evidence = json.loads(evidence_bytes.decode("utf-8"))
        assert set(evidence) == {
            "artifactKind",
            "packageManifestSha256",
            "packageRoot",
            "schemaVersion",
            "verifierResultCanonicalization",
            "verifierResultEncoding",
            "verifierResultJson",
            "verifierResult",
            "verifierResultSha256",
        }
        assert evidence["schemaVersion"] == 1
        assert evidence["artifactKind"] == "actorwright-package-verification"
        assert Path(evidence["packageRoot"]) == package_root.resolve()
        assert evidence["verifierResultEncoding"] == "utf-8"
        assert evidence["verifierResultCanonicalization"] == (
            "powershell-converttojson-depth-100-compress-lf"
        )
        assert json.loads(evidence["verifierResultJson"]) == valid_result
        assert evidence["packageManifestSha256"] == hashlib.sha256(
            (package_root / "manifest.json").read_bytes()
        ).hexdigest().upper()
        assert evidence["verifierResult"] == valid_result
        assert evidence["verifierResult"]["version"] == "1.0.0-preview.260"
        assert evidence["verifierResultSha256"] == hashlib.sha256(
            evidence["verifierResultJson"].encode("utf-8")
        ).hexdigest().upper()

        refused_overwrite = _invoke(package_root, valid_json)
        assert refused_overwrite.returncode != 0
        assert evidence_path.read_bytes() == evidence_bytes
        assert not list(evidence_root.glob(
            f".{package_root.name}.json.tmp-*"
        ))

        invalid_cases = {
            "failed": json.dumps({**valid_result, "status": "FAIL"}),
            "kernel-missing": json.dumps(
                {key: value for key, value in valid_result.items()
                 if key != "protocolV2Kernel"}
            ),
            "truncated": '{"status":"PASS",',
        }
        for name, invalid_json in invalid_cases.items():
            invalid_root = _package_fixture(tmp_path, name)
            owned_leaves.add(invalid_root.name)
            invalid_evidence = evidence_root / f"{invalid_root.name}.json"
            refused = _invoke(invalid_root, invalid_json)
            assert refused.returncode != 0, name
            assert not invalid_evidence.exists(), name

        unsafe_root = _package_fixture(tmp_path, "unsafe leaf")
        owned_leaves.add(unsafe_root.name)
        unsafe_evidence = evidence_root / f"{unsafe_root.name}.json"
        refused_unsafe = _invoke(unsafe_root, valid_json)
        assert refused_unsafe.returncode != 0
        assert not unsafe_evidence.exists()

        failed_publish_root = _package_fixture(tmp_path, "publish-failure")
        owned_leaves.add(failed_publish_root.name)
        failed_publish_evidence = evidence_root / f"{failed_publish_root.name}.json"
        forced_failure = _invoke(
            failed_publish_root,
            valid_json,
            "-TestHook",
            "FailBeforePublish",
        )
        assert forced_failure.returncode != 0
        assert not failed_publish_evidence.exists()
        assert not list(evidence_root.glob(
            f".{failed_publish_root.name}.json.tmp-*"
        ))

        swap_root = _package_fixture(tmp_path, "swap")
        owned_leaves.add(swap_root.name)
        swap_evidence = evidence_root / f"{swap_root.name}.json"
        swap_attempt = _invoke(
            swap_root,
            valid_json,
            "-TestHook",
            "AttemptManifestSwap",
        )
        assert swap_attempt.returncode == 0, swap_attempt.stderr
        assert swap_evidence.is_file()
        assert (swap_root / "manifest.json").is_file()
        assert not (swap_root / "manifest.json.swap-probe").exists()

    finally:
        if evidence_root.is_dir():
            owned_paths: set[Path] = set()
            for leaf in owned_leaves:
                owned_paths.add(evidence_root / f"{leaf}.json")
                owned_paths.update(evidence_root.glob(f".{leaf}.json.tmp-*"))
            for owned_path in owned_paths:
                assert owned_path.parent == evidence_root
                owned_path.unlink(missing_ok=True)


def test_rejects_reparse_package_manifest_on_ntfs(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "package verification manifest symlink")
    evidence_root = ROOT / "artifacts" / "package-verification"
    package_root = tmp_path / f"actorwright-observability-reparse-{uuid.uuid4().hex}"
    package_root.mkdir()
    target = tmp_path / "manifest-target.json"
    target.write_text(
        '{"schemaVersion":1,"product":"Actorwright"}\n', encoding="utf-8"
    )
    evidence_path = evidence_root / f"{package_root.name}.json"
    try:
        try:
            (package_root / "manifest.json").symlink_to(target)
        except (NotImplementedError, OSError) as error:
            filesystem_fixture_unavailable(
                tmp_path, filesystem, "package verification manifest symlink", error
            )
        refused = _invoke(
            package_root,
            json.dumps(_valid_verifier_result(), separators=(",", ":"), sort_keys=True),
        )
        assert refused.returncode != 0
        assert "manifest must be one ordinary file" in (
            refused.stdout + refused.stderr
        ).lower()
        assert not evidence_path.exists()
    finally:
        _remove_owned_evidence(evidence_root, package_root.name)


def test_rejects_reparse_package_root_on_ntfs(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "package verification directory symlink")
    evidence_root = ROOT / "artifacts" / "package-verification"
    target = _package_fixture(tmp_path, "directory-target")
    package_root = tmp_path / f"actorwright-observability-directory-link-{uuid.uuid4().hex}"
    evidence_path = evidence_root / f"{package_root.name}.json"
    try:
        try:
            package_root.symlink_to(target, target_is_directory=True)
        except (NotImplementedError, OSError) as error:
            filesystem_fixture_unavailable(
                tmp_path, filesystem, "package verification directory symlink", error
            )
        refused = _invoke(
            package_root,
            json.dumps(_valid_verifier_result(), separators=(",", ":"), sort_keys=True),
        )
        assert refused.returncode != 0
        assert "package root must be one ordinary directory" in (
            refused.stdout + refused.stderr
        ).lower()
        assert not evidence_path.exists()
    finally:
        _remove_owned_evidence(evidence_root, package_root.name)
