from __future__ import annotations

import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from typing import Any


MODULE_PATH = Path(__file__).resolve().parents[1] / "verify_registered_authority.py"
SPEC = importlib.util.spec_from_file_location(
    "verify_registered_authority", MODULE_PATH
)
assert SPEC is not None and SPEC.loader is not None
VERIFIER = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = VERIFIER
SPEC.loader.exec_module(VERIFIER)


COMMIT = "4b097b3d9c7c945a5396c5137bbb9a9beb17ceb8"
PREVIOUS = [f"legacy command {index:03d}" for index in range(122)] + [
    "npc create-from-jslot",
    "preview npc",
]
ADDED = [
    "facegen hair-regions analyze",
    "facegen hair-regions apply",
    "facegen hair-regions preview",
    "facegen hair-regions propose",
    "facegen hair-regions verify",
]
CURRENT = PREVIOUS + ADDED


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def _write(path: Path, content: bytes | str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if isinstance(content, str):
        path.write_text(content, encoding="utf-8")
    else:
        path.write_bytes(content)


class SyntheticAuthorityMixin:
    def setUp(self) -> None:
        self.original_source_count = VERIFIER.EXPECTED_SOURCE_FILE_COUNT
        VERIFIER.EXPECTED_SOURCE_FILE_COUNT = 3
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.repo_root = self.root / "repo"
        self.artifact_root = self.root / "artifacts"
        self.repo_project = self.repo_root / "projects" / "NpcManagerReimplementation"
        self.package_root = (
            self.artifact_root
            / "projects"
            / "NpcManagerReimplementation"
            / "04-packages"
            / "npcmanager-1.0.0-preview.224"
        )
        self.previous_cli = (
            self.artifact_root
            / "projects"
            / "NpcManagerReimplementation"
            / "04-packages"
            / "npcmanager-1.0.0-preview.223"
            / "cli"
            / "npcm.exe"
        )
        self._create_fixture()

    def tearDown(self) -> None:
        self.temp.cleanup()
        VERIFIER.EXPECTED_SOURCE_FILE_COUNT = self.original_source_count

    def _create_fixture(self) -> None:
        source_entries: list[tuple[str, bytes]] = []
        for index in range(3):
            relative = Path("src") / f"fixture-{index:04d}.txt"
            content = f"fixture-{index}\n".encode("utf-8")
            source_entries.append((relative.as_posix(), content))
            _write(self.repo_project / relative, content)
            _write(self.package_root / "source" / relative, content)

        package_entries = [("cli/npcm.exe", b"preview.224 cli\n")]
        package_entries.extend(source_entries)
        for relative, content in package_entries:
            _write(self.package_root / relative, content)
        _write(self.previous_cli, b"preview.223 cli\n")

        manifest_entries = [
            {
                "path": relative,
                "bytes": len(content),
                "sha256": hashlib.sha256(content).hexdigest(),
            }
            for relative, content in package_entries
        ]
        manifest_path = self.package_root / "package-manifest.json"
        _write(manifest_path, json.dumps({"files": manifest_entries}, indent=2))
        hash_lines = "".join(
            f"{entry['sha256']}  {entry['path']}\n" for entry in manifest_entries
        )
        hash_path = self.package_root / "package.hashes.sha256"
        _write(hash_path, hash_lines)

        archive = self.package_root.parent / "npcmanager-1.0.0-preview.224.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as handle:
            for relative, _ in package_entries:
                handle.write(self.package_root / relative, relative)
            handle.write(manifest_path, "package-manifest.json")
            handle.write(hash_path, "package.hashes.sha256")

        self.declaration = {
            "schemaVersion": 1,
            "evidenceId": "synthetic-preview224",
            "product": "NpcManagerReimplementation",
            "overallState": "M9_IN_PROGRESS",
            "registeredPackage": {
                "version": "1.0.0-preview.224",
                "expandedRoot": "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.224",
                "archive": "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.224.zip",
                "archiveSha256": _sha256(archive),
                "packageManifestSha256": _sha256(manifest_path),
                "hashManifestSha256": _sha256(hash_path),
                "manifestedFileCount": len(package_entries),
                "internalVersion": "0.1.0-m1",
                "registered": True,
                "previousExpandedRoot": "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.223",
            },
            "preservedSource": {
                "branch": "codex/npcmanager-dual-tone-hair",
                "commit": COMMIT,
                "tag": "npcmanager/preview.224-source",
                "snapshotRoot": "projects/NpcManagerReimplementation/04-packages/npcmanager-1.0.0-preview.224/source",
                "snapshotFileCount": 3,
                "snapshotMismatchCount": 0,
                "status": "FROZEN_PENDING_CONTROLLED_INTEGRATION",
            },
            "developmentSource": {
                "version": "preview.225-dev",
                "registered": False,
                "status": "NOT_YET_INTEGRATED",
            },
            "catalogue": {
                "previousVersion": "1.0.0-preview.223",
                "previousUniqueCommands": 124,
                "registeredUniqueCommands": 129,
                "exactAddedCommands": ADDED,
            },
            "verificationBaseline": {
                "suiteCount": 12,
                "declaredTestCount": 516,
            },
            "authority": {
                "staticGuiCoverage": "26/26",
                "canonicalJourneys": "2/4",
                "runtimeReleaseClaim": False,
                "blanketVisualAuthority": False,
            },
        }

    def _git_ref(self, ref: str) -> str:
        if ref == "npcmanager/preview.224-source":
            return COMMIT
        if ref.startswith("ancestor:"):
            return "true"
        return COMMIT

    def _capabilities(self, path: Path) -> dict[str, Any]:
        if "preview.223" in path.as_posix():
            return {"commands": PREVIOUS}
        return {"commands": CURRENT}

    def _run(self, declaration: dict[str, Any] | None = None, phase: str = "frozen-package") -> dict[str, Any]:
        return VERIFIER.verify_authority(
            self.repo_root,
            self.artifact_root,
            declaration or self.declaration,
            phase,
            git_ref_resolver=self._git_ref,
            capability_reader=self._capabilities,
        )

    def assert_failed(self, report: dict[str, Any], text: str) -> None:
        self.assertEqual(report["outcome"], "FAIL")
        self.assertTrue(
            any(text.casefold() in str(error).casefold() for error in report["errors"]),
            report["errors"],
        )


class FrozenAuthorityTests(SyntheticAuthorityMixin, unittest.TestCase):
    def test_accepts_exact_frozen_authority(self) -> None:
        report = self._run()
        self.assertEqual(report["outcome"], "PASS", report["errors"])
        self.assertTrue(report["observed"]["sourceRoot"].endswith("repo"))

    def test_rejects_archive_hash_mismatch(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        declaration["registeredPackage"]["archiveSha256"] = "0" * 64
        self.assert_failed(self._run(declaration), "archive hash")

    def test_rejects_manifest_inventory_mismatch(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        manifest_path = self.package_root / "package-manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        manifest["files"][0]["bytes"] += 1
        _write(manifest_path, json.dumps(manifest, indent=2))
        self.assert_failed(self._run(declaration), "manifest")

    def test_rejects_source_snapshot_mismatch(self) -> None:
        _write(self.package_root / "source" / "src" / "fixture-0000.txt", b"changed\n")
        self.assert_failed(self._run(), "source snapshot")

    def test_rejects_source_tag_at_wrong_commit(self) -> None:
        report = VERIFIER.verify_authority(
            self.repo_root,
            self.artifact_root,
            self.declaration,
            "frozen-package",
            git_ref_resolver=lambda ref: "wrong" if ref == "npcmanager/preview.224-source" else COMMIT,
            capability_reader=self._capabilities,
        )
        self.assert_failed(report, "tag")

    def test_accepts_separate_k_local_artifact_root(self) -> None:
        report = self._run()
        self.assertEqual(report["outcome"], "PASS", report["errors"])
        self.assertNotEqual(report["observed"]["sourceRoot"], report["observed"]["artifactRoot"])

    def test_rejects_f_artifact_root_without_access(self) -> None:
        report = VERIFIER.verify_authority(
            self.repo_root,
            Path("F:/ExampleGame"),
            self.declaration,
            "frozen-package",
            git_ref_resolver=lambda _ref: COMMIT,
            capability_reader=lambda _path: (_ for _ in ()).throw(AssertionError("reader called")),
        )
        self.assert_failed(report, "F:")


class CatalogueAndMetadataTests(SyntheticAuthorityMixin, unittest.TestCase):
    def test_rejects_missing_preview223_command(self) -> None:
        original = list(PREVIOUS)
        try:
            PREVIOUS.pop()
            self.assert_failed(self._run(), "catalogue")
        finally:
            PREVIOUS[:] = original

    def test_rejects_any_addition_outside_five_hair_region_phases(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        current = list(CURRENT) + ["unsupported addition"]
        report = VERIFIER.verify_authority(
            self.repo_root,
            self.artifact_root,
            declaration,
            "frozen-package",
            git_ref_resolver=self._git_ref,
            capability_reader=lambda path: {"commands": PREVIOUS if "223" in path.as_posix() else current},
        )
        self.assert_failed(report, "addition")

    def test_rejects_preview225_as_registered_package(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        declaration["registeredPackage"]["version"] = "1.0.0-preview.225"
        self.assert_failed(self._run(declaration), "registered package")

    def test_rejects_runtime_or_blanket_visual_authority(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        declaration["authority"]["runtimeReleaseClaim"] = True
        declaration["authority"]["blanketVisualAuthority"] = True
        self.assert_failed(self._run(declaration), "authority")

    def test_rejects_development_commit_outside_head_history(self) -> None:
        declaration = json.loads(json.dumps(self.declaration))
        declaration["developmentSource"].update(
            {"status": "INTEGRATED_DEVELOPMENT_SOURCE_PREVIEW225_DEV", "commit": "dev-commit"}
        )
        report = VERIFIER.verify_authority(
            self.repo_root,
            self.artifact_root,
            declaration,
            "post-integration",
            git_ref_resolver=lambda ref: "false" if ref == "ancestor:dev-commit" else COMMIT,
            capability_reader=self._capabilities,
        )
        self.assert_failed(report, "history")

    def test_refuses_to_overwrite_existing_report(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "report.json"
            output.write_text("sentinel", encoding="utf-8")
            with self.assertRaisesRegex(VERIFIER.AuthorityError, "already exists"):
                VERIFIER.write_new_report(output, {"outcome": "PASS"})
            self.assertEqual(output.read_text(encoding="utf-8"), "sentinel")


if __name__ == "__main__":
    unittest.main(verbosity=2)
