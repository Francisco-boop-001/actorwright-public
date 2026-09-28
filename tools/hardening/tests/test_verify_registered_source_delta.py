from __future__ import annotations

import importlib.util
import os
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "verify_registered_source_delta.py"
SPEC = importlib.util.spec_from_file_location("verify_registered_source_delta", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


class RegisteredSourceDeltaTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.previous = self.root / "previous" / "source"
        self.project = self.root / "project"
        self.previous.mkdir(parents=True)
        self.project.mkdir()

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _write_previous(self, relative: str, text: str = "same\n") -> None:
        path = self.previous / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="")

    def _write_working(self, relative: str, text: str = "same\n") -> None:
        path = self.project / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="")

    def _verify(self, allowlist: dict[str, object] | None = None) -> dict[str, object]:
        return MODULE.verify(
            self.previous,
            self.project,
            allowlist or {"schemaVersion": 1, "classifications": []},
            include=("src",),
        )

    def test_exact_snapshot_has_empty_delta(self) -> None:
        self._write_previous("src/NpcManager.Application/CommandContracts.cs")
        self._write_working("src/NpcManager.Application/CommandContracts.cs")
        result = self._verify()
        self.assertEqual("PASS", result["outcome"], result)
        self.assertEqual([], result["unclassified"])
        self.assertEqual([], result["stageableExactPaths"])

    def test_changed_source_requires_exact_classification(self) -> None:
        relative = "src/NpcManager.Application/CommandContracts.cs"
        self._write_previous(relative)
        self._write_working(relative, "changed\n")
        result = self._verify()
        self.assertEqual("FAIL", result["outcome"])
        self.assertEqual([relative], result["unclassified"])

        allowlist = {
            "schemaVersion": 1,
            "classifications": [
                {
                    "path": relative,
                    "disposition": "unrelated-existing-drift",
                    "reason": "fixture drift",
                }
            ],
        }
        admitted = self._verify(allowlist)
        self.assertEqual("PASS", admitted["outcome"], admitted)

    def test_untracked_collision_is_reported_and_not_stageable(self) -> None:
        relative = "src/NpcManager.Application/CommandContracts.cs"
        self._write_previous(relative, "registered\n")
        self._write_working(relative, "collision\n")
        result = self._verify()
        row = result["delta"][0]
        self.assertEqual("untracked", row["workingState"])
        self.assertEqual("collision", row["disposition"])
        self.assertEqual([relative], result["unclassified"])
        self.assertEqual([], result["stageableExactPaths"])

    def test_missing_file_is_unclassified(self) -> None:
        relative = "src/NpcManager.Application/CommandContracts.cs"
        self._write_previous(relative)
        result = self._verify()
        self.assertEqual("missing", result["delta"][0]["disposition"])
        self.assertEqual([relative], result["unclassified"])

    @unittest.skipUnless(hasattr(os, "symlink"), "symlinks unavailable")
    def test_reparse_point_is_rejected(self) -> None:
        self._write_previous("src/real.txt")
        link = self.project / "src" / "link.txt"
        link.parent.mkdir(parents=True, exist_ok=True)
        try:
            link.symlink_to(self.project / "src" / "real.txt")
        except (OSError, NotImplementedError):
            self.skipTest("symlink creation unavailable")
        with self.assertRaises(MODULE.SourceBoundaryError):
            self._verify()

    def test_path_outside_project_root_is_rejected(self) -> None:
        self._write_previous("src/ok.txt")
        with self.assertRaises(ValueError):
            MODULE.verify(
                self.previous,
                self.project,
                {
                    "schemaVersion": 1,
                    "classifications": [
                        {
                            "path": "../outside.txt",
                            "disposition": "unrelated-existing-drift",
                            "reason": "invalid",
                        }
                    ],
                },
                include=("src",),
            )

    def test_case_folded_duplicate_is_rejected(self) -> None:
        self._write_previous("src/Foo.cs")
        self._write_working("src/Foo.cs")
        with self.assertRaises(MODULE.SourceBoundaryError):
            MODULE.verify(
                self.previous,
                self.project,
                {"schemaVersion": 1, "classifications": []},
                include=("src", "SRC"),
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
