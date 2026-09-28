"""Independent verifier for the explicit game/Data-root preflight contract."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()
PROTECTED = Path(r"F:\ExampleGame").resolve()


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def load(path: Path) -> dict:
    resolved = path.resolve()
    if WORKSPACE not in resolved.parents:
        fail("JSON path outside K workspace")
    if resolved.is_symlink():
        fail("JSON path may not be a reparse point")
    try:
        value = json.loads(resolved.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"invalid JSON: {error}")
    if not isinstance(value, dict):
        fail("root must be an object")
    return value


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    parser.add_argument("--reason")
    args = parser.parse_args()

    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    if value.get("edition") not in {"fallout4", "skyrimse"}:
        fail("edition")
    if not isinstance(value.get("isAllowed"), bool):
        fail("isAllowed")
    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list):
        fail("diagnostics")
    roots = {}
    for field in ("workspaceRoot", "dataRoot", "outputRoot"):
        raw = value.get(field)
        if not isinstance(raw, str) or not os.path.isabs(raw) or "\x00" in raw:
            fail(f"{field} path")
        roots[field] = Path(raw).resolve()
        if roots[field].is_symlink():
            fail(f"{field} reparse point")

    errors = {item.get("code") for item in diagnostics if isinstance(item, dict) and item.get("severity") in {"error", "Error"}}
    if args.expect == "allowed":
        if not value["isAllowed"] or errors:
            fail("allowed result contains refusal")
        if not is_under(roots["workspaceRoot"], WORKSPACE):
            fail("workspace root outside K")
        if not is_under(roots["dataRoot"], roots["workspaceRoot"]):
            fail("Data root outside workspace")
        if not is_under(roots["outputRoot"], roots["workspaceRoot"]):
            fail("output root outside workspace")
        if is_under(roots["dataRoot"], roots["outputRoot"]) or is_under(roots["outputRoot"], roots["dataRoot"]):
            fail("read/write overlap")
    else:
        if value["isAllowed"] or not errors:
            fail("refused result lacks an error")
        if args.reason and args.reason not in errors:
            fail(f"missing refusal reason {args.reason}")
        if is_under(roots["dataRoot"], PROTECTED) and "protected-root-refused" not in errors:
            fail("protected root was not diagnosed")
    print(f"RESULT PASS preflight schema/roots expect={args.expect}")


if __name__ == "__main__":
    main()
