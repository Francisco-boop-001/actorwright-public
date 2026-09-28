#!/usr/bin/env python3
"""Audit feature-ledger proof links without changing implementation status.

The parity ledger is the finite inventory of upstream features.  Its structural
validator proves provenance and shape, while this audit proves that each
terminal claim points to local implementation and acceptance evidence that is
still present.  Missing links are review findings, never silently treated as
proof.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


BASELINE_COMMIT = "2f765c5dc09277f48321eb8cf80ad7925d4699a4"
LINE_SUFFIX = re.compile(r":\d+(?:-\d+)?$")


def _path_text(value: Any) -> str | None:
    if isinstance(value, str):
        text = value.strip()
        return text or None
    if isinstance(value, dict) and isinstance(value.get("path"), str):
        text = value["path"].strip()
        return text or None
    return None


def _as_paths(value: Any) -> list[str]:
    if isinstance(value, list):
        result: list[str] = []
        for item in value:
            path = _path_text(item)
            if path is not None:
                result.append(path)
        return result
    path = _path_text(value)
    return [path] if path is not None else []


def _resolve_project_path(project_root: Path, workspace_root: Path,
                          raw: str) -> tuple[Path | None, str | None]:
    normalized = raw.replace("\\", "/")
    without_lines = LINE_SUFFIX.sub("", normalized)
    candidate = Path(without_lines)
    if candidate.is_absolute() or ".." in candidate.parts:
        return None, "unsafe-path"
    project_candidate = (project_root / candidate).resolve(strict=False)
    project_base = project_root.resolve()
    if (project_candidate == project_base or project_base in project_candidate.parents) and project_candidate.is_file():
        return project_candidate, None
    workspace_candidate = (workspace_root / candidate).resolve(strict=False)
    workspace_base = workspace_root.resolve()
    if (workspace_candidate == workspace_base or workspace_base in workspace_candidate.parents) and workspace_candidate.is_file():
        return workspace_candidate, None
    if project_candidate != project_base and project_base not in project_candidate.parents:
        return None, "outside-project"
    return project_candidate, None


def _check_links(project_root: Path, workspace_root: Path, item: dict[str, Any], field: str,
                 findings: list[dict[str, str]]) -> list[str]:
    paths = _as_paths(item.get(field))
    if not paths:
        findings.append({
            "id": str(item.get("id", "?")),
            "kind": f"missing-{field.replace('_', '-')}",
            "field": field,
            "detail": f"{field} must name at least one local proof path for a terminal feature.",
        })
        return []
    for raw in paths:
        resolved, error = _resolve_project_path(project_root, workspace_root, raw)
        if error is not None:
            findings.append({
                "id": str(item.get("id", "?")),
                "kind": error,
                "field": field,
                "detail": raw,
            })
        elif resolved is None or not resolved.is_file():
            findings.append({
                "id": str(item.get("id", "?")),
                "kind": "missing-path",
                "field": field,
                "detail": raw,
            })
    return paths


def audit(data: dict[str, Any], project_root: Path) -> dict[str, Any]:
    workspace_root = project_root.parents[1]
    findings: list[dict[str, str]] = []
    terminal_items = [item for item in data.get("items", [])
                      if isinstance(item, dict) and item.get("implementation_status") in {"PASS", "NOT_APPLICABLE"}]
    for item in terminal_items:
        implementation = _check_links(project_root, workspace_root, item, "implementation_evidence", findings)
        acceptance = _check_links(project_root, workspace_root, item, "acceptance_evidence", findings)
        all_paths = implementation + acceptance
        if not any("tools/fixtures/" in path.replace("\\", "/") for path in all_paths):
            findings.append({
                "id": str(item.get("id", "?")),
                "kind": "fixture-link-missing",
                "field": "fixture_id",
                "detail": f"{item.get('fixture_id', '?')} has no explicit tools/fixtures proof link.",
            })
    return {
        "schemaVersion": 1,
        "status": "OPEN_EVIDENCE" if findings else "PASS",
        "result": "OPEN_EVIDENCE" if findings else "PASS",
        "baselineCommit": data.get("upstream_commit"),
        "expectedBaselineCommit": BASELINE_COMMIT,
        "evidenceRoots": ["project", "workspace"],
        "ledgerItems": len(data.get("items", [])),
        "terminalItemsAudited": len(terminal_items),
        "findingCount": len(findings),
        "findingKinds": {
            kind: sum(finding["kind"] == kind for finding in findings)
            for kind in sorted({finding["kind"] for finding in findings})
        },
        "interpretation": (
            "This is an evidence-integrity audit. OPEN_EVIDENCE means a feature may have code or a test, "
            "but the ledger does not link a complete, present proof chain. It does not downgrade the feature "
            "status automatically."
        ),
        "findings": findings,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("ledger", nargs="?", type=Path,
                        default=Path("05-reports/feature-ledger.json"))
    parser.add_argument("--report", type=Path)
    parser.add_argument("--strict", action="store_true",
                        help="return failure when any proof link is missing or stale")
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[1]
    ledger_path = args.ledger if args.ledger.is_absolute() else project_root / args.ledger
    try:
        data = json.loads(ledger_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"result": "FAIL", "errors": [str(exc)]}, indent=2))
        return 1
    result = audit(data, project_root)
    output = json.dumps(result, indent=2) + "\n"
    if args.report:
        report_path = args.report if args.report.is_absolute() else project_root / args.report
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(output, encoding="utf-8", newline="\n")
    print(output, end="")
    return 1 if args.strict and result["status"] != "PASS" else 0


if __name__ == "__main__":
    sys.exit(main())
