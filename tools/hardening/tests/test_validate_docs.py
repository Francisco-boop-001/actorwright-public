from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "validate_docs.py"
SPEC = importlib.util.spec_from_file_location("validate_docs", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
VALIDATE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = VALIDATE
SPEC.loader.exec_module(VALIDATE)


CURRENT_MARKERS = """M9_IN_PROGRESS
26/26
2/4
1.0.0-preview.227
runtime_release_claim=false
0.1.0-m1
pipeline preset-to-npc
gui --launch
"""


class AuthorityMarkerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / "docs").mkdir()
        (self.root / "tasks").mkdir()
        (self.root / "tools" / "templates").mkdir(parents=True)
        (self.root / "README.md").write_text(
            CURRENT_MARKERS + "[registration](05-reports/npc-manager-preview227-provisional-registration-20260803.json)\n",
            encoding="utf-8",
        )
        for name in ("build.md", "licenses.md", "threat-model.md", "runtime-smoke-kit.md"):
            (self.root / "docs" / name).write_text("current documentation\n", encoding="utf-8")
        (self.root / "docs" / "cli-contract.md").write_text(
            "pipeline preset-to-npc\n", encoding="utf-8"
        )
        (self.root / "tasks" / "todo.md").write_text("task trail\n", encoding="utf-8")
        for name in ("runtime-smoke-fo4.txt", "runtime-smoke-sse.txt"):
            (self.root / "tools" / "templates" / name).write_text("template\n", encoding="utf-8")
        (self.root / "05-reports").mkdir()
        (self.root / "05-reports" / "npc-manager-preview227-provisional-registration-20260803.json").write_text(
            "registration\n", encoding="utf-8"
        )

    def tearDown(self) -> None:
        self.temp.cleanup()

    def _validate(self) -> dict[str, object]:
        return VALIDATE.validate(self.root)

    def test_accepts_current_authority_markers(self) -> None:
        result = self._validate()
        self.assertEqual(result["status"], "PASS", result["errors"])

    def test_rejects_stale_zero_of_twenty_six_gui_marker(self) -> None:
        readme = self.root / "README.md"
        readme.write_text(readme.read_text(encoding="utf-8").replace("26/26", "0/26"), encoding="utf-8")
        result = self._validate()
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("stale GUI" in error for error in result["errors"]))

    def test_rejects_missing_registered_preview227_marker(self) -> None:
        readme = self.root / "README.md"
        readme.write_text(readme.read_text(encoding="utf-8").replace("1.0.0-preview.227", "package-current"), encoding="utf-8")
        result = self._validate()
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("registered package" in error for error in result["errors"]))

    def test_rejects_runtime_release_claim_true(self) -> None:
        readme = self.root / "README.md"
        readme.write_text(readme.read_text(encoding="utf-8").replace("runtime_release_claim=false", "runtime_release_claim=true"), encoding="utf-8")
        result = self._validate()
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("runtime" in error for error in result["errors"]))

    def test_rejects_missing_registration_link(self) -> None:
        readme = self.root / "README.md"
        readme.write_text(readme.read_text(encoding="utf-8").replace("[registration](05-reports/npc-manager-preview227-provisional-registration-20260803.json)", "registration"), encoding="utf-8")
        result = self._validate()
        self.assertEqual(result["status"], "FAIL")
        self.assertTrue(any("registration report" in error for error in result["errors"]))


if __name__ == "__main__":
    unittest.main(verbosity=2)
