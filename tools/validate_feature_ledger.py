#!/usr/bin/env python3
"""Validate the finite v1.0 feature-parity ledger.

This validator deliberately checks structure, baseline provenance, evidence
paths, CLI coverage, and terminal-state semantics. It does not claim that a
feature is implemented; implementation proof is recorded separately by gates.
"""

from __future__ import annotations

import argparse
import copy
import json
import re
import sys
import zipfile
from collections import Counter
from pathlib import Path
from typing import Any


EXPECTED_COMMIT = "2f765c5dc09277f48321eb8cf80ad7925d4699a4"
EXPECTED_FAMILIES = {f"P{number:02d}" for number in range(1, 13)}
VALID_GAMES = {"fallout4", "skyrimse"}
VALID_KINDS = {"parity", "product"}
VALID_STATUSES = {"PLANNED", "PASS", "FAIL", "BLOCKED", "NOT_APPLICABLE"}
TERMINAL_STATUSES = {"PASS", "NOT_APPLICABLE"}
MILESTONE_PATTERN = re.compile(r"^M[1-9]$")
REQUIRED_ITEM_FIELDS = {
    "id",
    "family",
    "name",
    "requirement_kind",
    "games",
    "evidence",
    "application_use_case",
    "cli_command",
    "fixture_id",
    "acceptance_rule",
    "milestone",
    "implementation_status",
}


def _nonempty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _project_path(project_root: Path, relative: Any) -> Path | None:
    if not _nonempty_string(relative):
        return None
    candidate_text = str(relative)
    if "\\" in candidate_text:
        return None
    pure = Path(candidate_text)
    if pure.is_absolute() or ".." in pure.parts:
        return None
    candidate = (project_root / pure).resolve(strict=False)
    root = project_root.resolve()
    if candidate != root and root not in candidate.parents:
        return None
    return candidate


def validate_ledger(data: dict[str, Any], project_root: Path) -> list[str]:
    errors: list[str] = []
    if data.get("schema_version") != 1:
        errors.append("schema_version must equal 1")
    if data.get("upstream_commit") != EXPECTED_COMMIT:
        errors.append(f"upstream_commit must equal {EXPECTED_COMMIT}")

    archive_relative = data.get("source_archive")
    archive_path = _project_path(project_root, archive_relative)
    archive_entries: set[str] = set()
    if archive_path is None or not archive_path.is_file():
        errors.append("source_archive must name an existing project-local archive")
    else:
        try:
            with zipfile.ZipFile(archive_path) as source_zip:
                archive_entries = {
                    info.filename
                    for info in source_zip.infolist()
                    if not info.is_dir()
                }
        except (OSError, zipfile.BadZipFile) as exc:
            errors.append(f"source_archive cannot be read: {exc}")

    items = data.get("items")
    if not isinstance(items, list) or not items:
        return errors + ["items must be a non-empty array"]

    ids: list[str] = []
    fixtures: list[str] = []
    families: Counter[str] = Counter()
    statuses: Counter[str] = Counter()
    kinds: Counter[str] = Counter()

    for index, item in enumerate(items):
        label = f"items[{index}]"
        if not isinstance(item, dict):
            errors.append(f"{label} must be an object")
            continue

        missing = sorted(REQUIRED_ITEM_FIELDS - item.keys())
        if missing:
            errors.append(f"{label} missing fields: {', '.join(missing)}")
            continue

        item_id = item.get("id")
        family = item.get("family")
        fixture_id = item.get("fixture_id")
        requirement_kind = item.get("requirement_kind")
        status = item.get("implementation_status")

        if not _nonempty_string(item_id):
            errors.append(f"{label}.id must be a non-empty string")
        else:
            ids.append(item_id)
            if not re.fullmatch(r"P\d{2}-\d{3}", item_id):
                errors.append(f"{label}.id must use PNN-NNN format")
            if family in EXPECTED_FAMILIES and not item_id.startswith(f"{family}-"):
                errors.append(f"{label}.id must start with {family}-")

        if family not in EXPECTED_FAMILIES:
            errors.append(f"{label}.family is invalid: {family!r}")
        else:
            families[family] += 1

        if requirement_kind not in VALID_KINDS:
            errors.append(f"{label}.requirement_kind is invalid: {requirement_kind!r}")
        else:
            kinds[requirement_kind] += 1

        games = item.get("games")
        if not isinstance(games, list) or not games or set(games) - VALID_GAMES:
            errors.append(f"{label}.games must be a non-empty subset of {sorted(VALID_GAMES)}")

        for field in ("name", "application_use_case", "cli_command", "fixture_id", "acceptance_rule"):
            if not _nonempty_string(item.get(field)):
                errors.append(f"{label}.{field} must be a non-empty string")
        if _nonempty_string(fixture_id):
            fixtures.append(fixture_id)
        if _nonempty_string(item.get("cli_command")) and not item["cli_command"].startswith("npcm "):
            errors.append(f"{label}.cli_command must start with 'npcm '")

        milestone = item.get("milestone")
        if not _nonempty_string(milestone) or not MILESTONE_PATTERN.fullmatch(milestone):
            errors.append(f"{label}.milestone must be M1 through M9")

        if status not in VALID_STATUSES:
            errors.append(f"{label}.implementation_status is invalid: {status!r}")
        else:
            statuses[status] += 1
        if status == "NOT_APPLICABLE":
            approval = item.get("not_applicable_approval")
            if not isinstance(approval, dict) or not all(
                _nonempty_string(approval.get(field)) for field in ("approved_by", "date", "reason")
            ):
                errors.append(f"{label} requires explicit approval for NOT_APPLICABLE")

        evidence = item.get("evidence")
        if not isinstance(evidence, list) or not evidence:
            errors.append(f"{label}.evidence must be a non-empty array")
            continue
        for evidence_index, entry in enumerate(evidence):
            evidence_label = f"{label}.evidence[{evidence_index}]"
            if not isinstance(entry, dict):
                errors.append(f"{evidence_label} must be an object")
                continue
            source = entry.get("source")
            path = entry.get("path")
            locator = entry.get("locator")
            if source not in {"upstream", "goal"}:
                errors.append(f"{evidence_label}.source must be upstream or goal")
            if not _nonempty_string(path) or not _nonempty_string(locator):
                errors.append(f"{evidence_label} requires non-empty path and locator")
                continue
            if source == "upstream" and path not in archive_entries:
                errors.append(f"{evidence_label}.path is absent from source archive: {path}")
            if source == "goal":
                goal_path = _project_path(project_root, path)
                if goal_path is None:
                    errors.append(f"{evidence_label}.path escapes the project or uses an invalid separator: {path}")
                elif not goal_path.is_file():
                    errors.append(f"{evidence_label}.path is absent from project: {path}")
        if requirement_kind == "parity" and not any(
            isinstance(entry, dict) and entry.get("source") == "upstream" for entry in evidence
        ):
            errors.append(f"{label} parity item lacks upstream evidence")
        if requirement_kind == "product" and not any(
            isinstance(entry, dict) and entry.get("source") == "goal" for entry in evidence
        ):
            errors.append(f"{label} product item lacks Goal evidence")

    duplicate_ids = sorted(key for key, count in Counter(ids).items() if count > 1)
    duplicate_fixtures = sorted(key for key, count in Counter(fixtures).items() if count > 1)
    if duplicate_ids:
        errors.append(f"duplicate item ids: {', '.join(duplicate_ids)}")
    if duplicate_fixtures:
        errors.append(f"duplicate fixture ids: {', '.join(duplicate_fixtures)}")
    missing_families = sorted(EXPECTED_FAMILIES - families.keys())
    if missing_families:
        errors.append(f"families without items: {', '.join(missing_families)}")
    if "UNKNOWN" in statuses:
        errors.append("UNKNOWN is forbidden as an implementation status")

    declared_count = data.get("item_count")
    if declared_count != len(items):
        errors.append(f"item_count is {declared_count!r}, expected {len(items)}")
    return errors


def summary(data: dict[str, Any]) -> dict[str, Any]:
    items = data["items"]
    return {
        "result": "PASS",
        "item_count": len(items),
        "family_counts": dict(sorted(Counter(item["family"] for item in items).items())),
        "kind_counts": dict(sorted(Counter(item["requirement_kind"] for item in items).items())),
        "status_counts": dict(sorted(Counter(item["implementation_status"] for item in items).items())),
        "terminal_item_count": sum(item["implementation_status"] in TERMINAL_STATUSES for item in items),
    }


def run_self_test(data: dict[str, Any], project_root: Path) -> list[str]:
    failures: list[str] = []
    unknown = copy.deepcopy(data)
    unknown["items"][0]["implementation_status"] = "UNKNOWN"
    if not validate_ledger(unknown, project_root):
        failures.append("UNKNOWN mutation was accepted")

    duplicate = copy.deepcopy(data)
    duplicate["items"][1]["id"] = duplicate["items"][0]["id"]
    if not validate_ledger(duplicate, project_root):
        failures.append("duplicate id mutation was accepted")

    missing_evidence = copy.deepcopy(data)
    missing_evidence["items"][0]["evidence"] = []
    if not validate_ledger(missing_evidence, project_root):
        failures.append("missing evidence mutation was accepted")

    escaped_evidence = copy.deepcopy(data)
    product_index = next(
        index for index, item in enumerate(escaped_evidence["items"])
        if item["requirement_kind"] == "product"
    )
    escaped_evidence["items"][product_index]["evidence"][0]["path"] = "../PROJECT_MANIFEST.json"
    if not validate_ledger(escaped_evidence, project_root):
        failures.append("project-escaping Goal evidence mutation was accepted")
    return failures


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "ledger",
        nargs="?",
        type=Path,
        help="Ledger path; defaults to 05-reports/feature-ledger.json",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    project_root = Path(__file__).resolve().parents[1]
    ledger_path = args.ledger or project_root / "05-reports" / "feature-ledger.json"
    if not ledger_path.is_absolute():
        ledger_path = Path.cwd() / ledger_path
    try:
        data = json.loads(ledger_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"result": "FAIL", "errors": [str(exc)]}, indent=2))
        return 1

    errors = validate_ledger(data, project_root)
    if not errors and args.self_test:
        errors.extend(run_self_test(data, project_root))
    if errors:
        print(json.dumps({"result": "FAIL", "errors": errors}, indent=2))
        return 1
    result = summary(data)
    if args.self_test:
        result["self_test"] = "PASS"
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
