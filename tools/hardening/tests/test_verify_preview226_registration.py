from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

MODULE_PATH = Path(__file__).resolve().parents[1] / "verify_preview226_registration.py"
SPEC = importlib.util.spec_from_file_location("verify_preview226_registration", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)
verify = MODULE.verify


def _write_package(root: Path, name: str, version: str) -> Path:
    package = root / name
    (package / "cli").mkdir(parents=True)
    executable = package / "cli" / "npcm.exe"
    executable.write_bytes(b"fixture executable")
    digest = hashlib.sha256(executable.read_bytes()).hexdigest()
    manifest = {
        "schemaVersion": 1,
        "version": version,
        "status": "PASS_WITH_SCOPED_LIMITS",
        "runtimeReleaseClaim": False,
        "files": [{"path": "cli/npcm.exe", "bytes": executable.stat().st_size, "sha256": digest}],
    }
    manifest_path = package / "package-manifest.json"
    manifest_path.write_text(json.dumps(manifest, sort_keys=True), encoding="utf-8")
    hashes_path = package / "package.hashes.sha256"
    hashes_path.write_text(f"{digest}  cli/npcm.exe\n", encoding="utf-8")
    with zipfile.ZipFile(package.with_suffix(".zip"), "w") as archive:
        archive.write(executable, "cli/npcm.exe")
        archive.writestr("package-manifest.json", manifest_path.read_bytes())
        archive.writestr("package.hashes.sha256", hashes_path.read_bytes())
    return package


class Preview226RegistrationTests(unittest.TestCase):
    def test_exact_command_delta_passes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            previous = _write_package(root, "previous", "1.0.0-preview.225")
            candidate = _write_package(root, "candidate", "1.0.0-preview.226")
            source_delta = root / "source-delta.json"
            source_delta.write_text(
                json.dumps({"outcome": "PASS", "workingProject": str(candidate / "source")}),
                encoding="utf-8",
            )
            previous_names = {f"command {index}" for index in range(130)}
            current_names = previous_names | {
                "npc finish analyze",
                "npc finish apply",
                "npc finish verify",
            }
            class Result:
                returncode = 0
                stderr = ""

                def __init__(self, names: set[str]) -> None:
                    self.stdout = json.dumps({"commands": sorted(names)})

            def run(command: list[str], **_: object) -> Result:
                return Result(previous_names if "previous" in command[0] else current_names)

            with patch("verify_preview226_registration.subprocess.run", side_effect=run):
                report = verify(root, previous, candidate, source_delta)
            self.assertEqual("PASS", report["outcome"])

    def test_command_removal_blocks_candidate(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            previous = _write_package(root, "previous", "1.0.0-preview.225")
            candidate = _write_package(root, "candidate", "1.0.0-preview.226")
            source_delta = root / "source-delta.json"
            source_delta.write_text(
                json.dumps({"outcome": "PASS", "workingProject": str(candidate / "source")}),
                encoding="utf-8",
            )
            previous_names = {f"command {index}" for index in range(130)}
            current_names = (previous_names - {"command 0"}) | {
                "npc finish analyze",
                "npc finish apply",
                "npc finish verify",
            }
            class Result:
                returncode = 0
                stderr = ""

                def __init__(self, names: set[str]) -> None:
                    self.stdout = json.dumps({"commands": sorted(names)})

            def run(command: list[str], **_: object) -> Result:
                return Result(previous_names if "previous" in command[0] else current_names)

            with patch("verify_preview226_registration.subprocess.run", side_effect=run):
                report = verify(root, previous, candidate, source_delta)
            self.assertEqual("FAIL", report["outcome"])
            self.assertTrue(any("command catalogue mismatch" in error for error in report["errors"]))


if __name__ == "__main__":
    unittest.main()
