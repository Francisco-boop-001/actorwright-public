from __future__ import annotations

import importlib.util
import os
import sys
import tempfile
import unittest
from unittest import mock
from pathlib import Path


MODULE_PATH = Path(__file__).resolve().parents[1] / "run_test_evidence.py"
SPEC = importlib.util.spec_from_file_location("run_test_evidence", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
RUNNER = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = RUNNER
SPEC.loader.exec_module(RUNNER)


class TerminalResultTests(unittest.TestCase):
    def test_accepts_one_complete_terminal_result(self) -> None:
        result = RUNNER.parse_terminal_result(
            "PASS first\nPASS second\nRESULT PASS 2/2\n"
        )
        self.assertEqual((result.passed, result.total), (2, 2))

    def test_rejects_failing_terminal_result(self) -> None:
        with self.assertRaisesRegex(RUNNER.EvidenceError, "not a complete pass"):
            RUNNER.parse_terminal_result("RESULT FAIL 1/2\n")

    def test_rejects_malformed_terminal_result(self) -> None:
        with self.assertRaisesRegex(RUNNER.EvidenceError, "exactly one terminal"):
            RUNNER.parse_terminal_result("PASS first\nall good\n")

    def test_rejects_duplicate_terminal_result(self) -> None:
        with self.assertRaisesRegex(RUNNER.EvidenceError, "exactly one terminal"):
            RUNNER.parse_terminal_result(
                "RESULT PASS 1/1\nRESULT PASS 1/1\n"
            )


class ProcessTests(unittest.TestCase):
    def test_timeout_is_fail_closed_and_captures_partial_output(self) -> None:
        command = [
            sys.executable,
            "-c",
            "import sys,time; print('started', flush=True); "
            "time.sleep(2); sys.exit(0)",
        ]
        outcome = RUNNER.run_process(command, Path.cwd(), timeout_seconds=0.1)
        self.assertTrue(outcome.timed_out)
        self.assertNotEqual(outcome.exit_code, 0)
        self.assertIn("started", outcome.stdout)
        self.assertGreater(outcome.elapsed_seconds, 0)

    def test_missing_tool_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            missing = Path(directory) / "missing.exe"
            with self.assertRaisesRegex(RUNNER.EvidenceError, "tool is missing"):
                RUNNER.require_file(missing, "coverage tool")

    def test_environment_override_reaches_only_the_child_process(self) -> None:
        variable = "NPCMANAGER_TEST_EVIDENCE_CHILD_VALUE"
        self.assertNotIn(variable, os.environ)
        outcome = RUNNER.run_process(
            [
                sys.executable,
                "-c",
                (
                    "import os; "
                    f"print(os.environ.get('{variable}', 'MISSING'))"
                ),
            ],
            Path.cwd(),
            timeout_seconds=10,
            environment={variable: "authority-bound"},
        )
        self.assertEqual(outcome.exit_code, 0)
        self.assertEqual(outcome.stdout.strip(), "authority-bound")
        self.assertNotIn(variable, os.environ)


class ToolWorkspaceTests(unittest.TestCase):
    def test_uses_local_tool_workspace_when_launcher_exists(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace = Path(directory) / "worktree"
            launcher = (
                workspace
                / "tools"
                / "external"
                / "dotnet-sdk-10.0.301-win-x64"
                / "dotnet.exe"
            )
            launcher.parent.mkdir(parents=True)
            launcher.write_bytes(b"local")
            with mock.patch.object(
                RUNNER,
                "_git_value",
                side_effect=AssertionError(
                    "Git common-dir lookup should not run for local tools"
                ),
            ):
                selected = RUNNER._find_tool_workspace_root(workspace)
            self.assertEqual(selected, workspace.resolve())

    def test_uses_canonical_git_workspace_without_copying_tools(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workspace = root / "worktree"
            canonical = root / "canonical"
            workspace.mkdir()
            (canonical / ".git").mkdir(parents=True)
            (canonical / "AGENTS.md").write_text(
                "authority",
                encoding="utf-8",
            )
            (canonical / "WORKSPACE_MANIFEST.json").write_text(
                "{}",
                encoding="utf-8",
            )
            launcher = (
                canonical
                / "tools"
                / "external"
                / "dotnet-sdk-10.0.301-win-x64"
                / "dotnet.exe"
            )
            launcher.parent.mkdir(parents=True)
            launcher.write_bytes(b"canonical")
            with mock.patch.object(
                RUNNER,
                "_git_value",
                return_value=str(canonical / ".git"),
            ):
                selected = RUNNER._find_tool_workspace_root(workspace)
            self.assertEqual(selected, canonical.resolve())


class TestAuthorityWorkspaceTests(unittest.TestCase):
    def test_uses_canonical_workspace_when_worktree_omits_authentic_fixtures(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            worktree = root / "worktree"
            canonical = root / "canonical"
            worktree.mkdir()
            canonical.mkdir()
            for relative in RUNNER.REQUIRED_TEST_AUTHORITY_PATHS:
                marker = canonical / relative
                marker.parent.mkdir(parents=True, exist_ok=True)
                marker.write_bytes(b"fixture")
            selected = RUNNER._find_test_authority_workspace_root(
                worktree,
                canonical,
            )
            self.assertEqual(selected, canonical.resolve())

    def test_refuses_workspace_when_authentic_fixture_authority_is_incomplete(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            worktree = root / "worktree"
            canonical = root / "canonical"
            worktree.mkdir()
            canonical.mkdir()
            with self.assertRaisesRegex(
                RUNNER.EvidenceError,
                "authentic test authority",
            ):
                RUNNER._find_test_authority_workspace_root(
                    worktree,
                    canonical,
                )


class ToolRootTests(unittest.TestCase):
    def _workspace(self, directory: str) -> Path:
        workspace = Path(directory) / "workspace"
        (workspace / "tools" / "external").mkdir(parents=True)
        (workspace / "AGENTS.md").write_text("instructions\n", encoding="utf-8")
        (workspace / "WORKSPACE_MANIFEST.json").write_text("{}\n", encoding="utf-8")
        return workspace

    def test_default_tool_root_is_workspace_external(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace = self._workspace(directory)
            with mock.patch.object(RUNNER, "_find_workspace_root", return_value=workspace):
                self.assertEqual(
                    RUNNER._resolve_tool_root(Path(directory) / "project"),
                    (workspace / "tools" / "external").resolve(),
                )

    def test_explicit_k_local_tool_root_is_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace = self._workspace(directory)
            explicit = Path(directory) / "copied-tools" / "external"
            with mock.patch.object(RUNNER, "_find_workspace_root", return_value=workspace):
                self.assertEqual(
                    RUNNER._resolve_tool_root(Path(directory) / "project", explicit),
                    explicit.resolve(),
                )

    def test_explicit_f_root_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            workspace = self._workspace(directory)
            with mock.patch.object(RUNNER, "_find_workspace_root", return_value=workspace):
                with self.assertRaisesRegex(RUNNER.EvidenceError, "F:"):
                    RUNNER._resolve_tool_root(Path(directory) / "project", Path("F:/ExampleGame/tools/external"))

    def test_no_restore_adds_exactly_one_build_argument(self) -> None:
        command = RUNNER._build_command(Path("dotnet.exe"), Path("project"), "Release", True)
        self.assertEqual(command.count("--no-restore"), 1)

    def test_evidence_source_record_contains_source_and_tool_roots(self) -> None:
        source = RUNNER._source_record(Path("source"), Path("tools"), "commit", "M path")
        self.assertEqual(source["root"], str(Path("source").resolve()))
        self.assertEqual(source["toolRoot"], str(Path("tools").resolve()))


class ReportTests(unittest.TestCase):
    def test_existing_output_is_rejected_without_change(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            existing = Path(directory) / "evidence.json"
            existing.write_text("sentinel", encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.EvidenceError, "already exists"):
                RUNNER.require_new_outputs([existing])
            self.assertEqual(existing.read_text(encoding="utf-8"), "sentinel")

    def test_invalid_cobertura_report_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "coverage.cobertura.xml"
            report.write_text("<coverage><packages>", encoding="utf-8")
            with self.assertRaisesRegex(RUNNER.EvidenceError, "invalid Cobertura"):
                RUNNER.read_cobertura_inventory(
                    report,
                    ("NpcManager.Application",),
                )

    def test_cobertura_inventory_reports_loaded_and_unloaded_assemblies(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.5" branch-rate="0.25">
  <packages>
    <package name="NpcManager.Application" line-rate="0.5"
             branch-rate="0.25" />
  </packages>
</coverage>
"""
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / "coverage.cobertura.xml"
            report.write_text(xml, encoding="utf-8")
            inventory = RUNNER.read_cobertura_inventory(
                report,
                ("NpcManager.Application", "NpcManager.Rendering"),
            )
        by_name = {row["assembly"]: row for row in inventory}
        self.assertTrue(by_name["NpcManager.Application"]["loaded"])
        self.assertEqual(by_name["NpcManager.Application"]["lineRate"], 0.5)
        self.assertFalse(by_name["NpcManager.Rendering"]["loaded"])
        self.assertIsNone(by_name["NpcManager.Rendering"]["lineRate"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
