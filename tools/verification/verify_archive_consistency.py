"""Independently verify a selected-plugin archive-consistency CLI artifact."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace").resolve()
SUPPORTED = {"fallout4": ".ba2", "skyrimse": ".bsa"}
STATUSES = {"matched", "notIndexed", "missingOnDisk", "unsupportedEdition"}


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def load(path: Path) -> dict:
    resolved = path.resolve()
    if not is_under(resolved, WORKSPACE):
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


def path_field(value: object, name: str) -> Path:
    if not isinstance(value, str) or not os.path.isabs(value) or "\x00" in value:
        fail(f"{name} path")
    path = Path(value).resolve()
    if not is_under(path, WORKSPACE):
        fail(f"{name} outside K workspace")
    if path.is_symlink():
        fail(f"{name} may not be a reparse point")
    return path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--json", required=True, type=Path)
    parser.add_argument("--expect", choices=("allowed", "refused"), required=True)
    parser.add_argument("--reason")
    args = parser.parse_args()

    value = load(args.json)
    if value.get("schemaVersion") != "1":
        fail("schema version")
    edition = value.get("edition")
    if edition not in SUPPORTED:
        fail("edition")
    if not isinstance(value.get("isConsistent"), bool):
        fail("isConsistent")
    plugin = path_field(value.get("plugin"), "plugin")
    asset_index = path_field(value.get("assetIndex"), "assetIndex")
    data_root_value = value.get("dataRoot")
    data_root = path_field(data_root_value, "dataRoot") if data_root_value is not None else None
    if data_root is not None and plugin.parent != data_root:
        fail("plugin/data-root alignment")
    if not isinstance(value.get("archives"), list):
        fail("archives")
    diagnostics = value.get("diagnostics")
    if not isinstance(diagnostics, list):
        fail("diagnostics")

    archives = value["archives"]
    previous: tuple[str, str] | None = None
    names: set[str] = set()
    errors: set[object] = set()
    for item in diagnostics:
        if isinstance(item, dict) and item.get("severity") in {"error", "Error"}:
            errors.add(item.get("code"))
    for archive in archives:
        if not isinstance(archive, dict) or set(archive) != {"archiveName", "presentOnDisk", "indexed", "status"}:
            fail("archive fields")
        name = archive["archiveName"]
        if not isinstance(name, str) or not name or "/" in name or "\\" in name or Path(name).name != name:
            fail("archive name")
        if name.casefold() in names:
            fail("duplicate archive")
        names.add(name.casefold())
        if not isinstance(archive["presentOnDisk"], bool) or not isinstance(archive["indexed"], bool):
            fail("archive booleans")
        status = archive["status"]
        if status not in STATUSES:
            fail("archive status")
        sort_key = (name.casefold(), name)
        if previous is not None and sort_key < previous:
            fail("archive ordering")
        previous = sort_key

    if args.expect == "allowed":
        if not value["isConsistent"] or errors:
            fail("allowed result contains errors")
        if data_root is None or not is_under(data_root, WORKSPACE) or not plugin.is_file() or not asset_index.is_file():
            fail("allowed roots/files")
        extension = SUPPORTED[edition]
        actual = {path.name.casefold() for path in data_root.iterdir() if path.is_file() and path.suffix.casefold() == extension}
        if actual != names:
            fail("allowed archive set differs from disk")
        if any(item["status"] != "matched" or not item["presentOnDisk"] or not item["indexed"] for item in archives):
            fail("allowed archive status")
    else:
        if value["isConsistent"] or not errors:
            fail("refused result lacks an error")
        if args.reason and args.reason not in errors:
            fail(f"missing refusal reason {args.reason}")

    print(f"RESULT PASS archive-consistency schema/archives={len(archives)} expect={args.expect}")


if __name__ == "__main__":
    main()
